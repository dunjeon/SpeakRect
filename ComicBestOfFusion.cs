using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SpeakRect
{
    /// <summary>
    /// Comic Book best-of / speak-unit fusion: full-frame vs crop primary, residual
    /// novel spans, geometry-guided full-frame split. Pure string/geometry logic —
    /// no HTTP, no WinRT OCR, no TTS. Called from <see cref="OcrProcessor"/>.
    /// </summary>
    public static class ComicBestOfFusion
    {
        /// <summary>Minimum words for a speak unit to be kept (matches live pipeline).</summary>
        public const int MinSpeakUnitWords = 1;

        private static string Truncate(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length <= max) return s;
            return s.Substring(0, Math.Max(0, max - 1)) + "…";
        }
        /// <summary>Usable crop text with reading-order region index.</summary>
        public readonly struct CropRead
        {
            public int RegionIndex { get; }
            public string Text { get; }
            public CropRead(int regionIndex, string text)
            {
                RegionIndex = regionIndex;
                Text = text;
            }
        }

        /// <summary>
        /// Choose full-frame vs crops for ComicBook best-of.
        /// When detect is solid: <b>crop-primary</b> (reading-order crops, full only
        /// for gaps). When scrapy/weak: full-primary with crop repair + novel inserts.
        /// </summary>
        public static (List<string> Chosen, string Tag) PickBestOfFullVsCrops(
            List<string> fullParts,
            List<CropRead> cropReads,
            StringBuilder detail,
            bool scrapDetect = false,
            bool solidIslands = false,
            int readingBlocks = 0)
        {
            int fullWords = fullParts.Sum(ComicRegionGeometry.CountWords);
            int cropWordsRaw = cropReads.Sum(c => ComicRegionGeometry.CountWords(c.Text));
            bool fullOk = fullParts.Count > 0 && fullWords >= 2;
            bool cropsOk = cropReads.Count > 0 && cropWordsRaw >= 4;

            if (!fullOk && !cropsOk)
            {
                detail.AppendLine("winner=none (full and crops empty)");
                return (new List<string>(), "none");
            }

            if (!fullOk)
            {
                var onlyCrops = cropReads
                    .OrderBy(c => c.RegionIndex)
                    .Select(c => c.Text)
                    .ToList();
                detail.AppendLine(
                    $"winner=crops words={cropWordsRaw} parts={onlyCrops.Count} (full empty/weak)");
                return (onlyCrops, "crops");
            }

            if (!cropsOk)
            {
                detail.AppendLine(
                    $"winner=full-frame words={fullWords} parts={fullParts.Count} (crops empty)");
                return (fullParts, "full-frame");
            }

            var fullUnitsProbe = SpeechCleaner.ExpandToSpeakUnits(fullParts);

            // Phase B: full-frame owns balloon order; crops supply better wording.
            // Prefer this even when detect is scrapy if full-frame balloon split is solid
            // (over-split pixel islands must not demote a good full unit sequence).
            bool fullOrderOk =
                cropsOk &&
                fullUnitsProbe.Count >= 2 &&
                fullWords >= 8 &&
                (!scrapDetect || fullUnitsProbe.Count >= 5);
            if (fullOrderOk)
            {
                detail.AppendLine(
                    $"strategy=full-order+crops (fullUnits={fullUnitsProbe.Count} " +
                    $"cropOk={cropReads.Count} solid={solidIslands} scrap={scrapDetect})");
                var ordered = BuildFullOrderCropWordingSpeakUnits(
                    fullParts, cropReads, detail);
                int orderedWords = ordered.Sum(ComicRegionGeometry.CountWords);
                if (ordered.Count >= 2 && orderedWords >= 4)
                {
                    detail.AppendLine(
                        $"winner=full-order+crops units={ordered.Count} " +
                        $"words={orderedWords} " +
                        $"(full had {fullWords} words / {fullUnitsProbe.Count} units; " +
                        $"snipWords={cropWordsRaw})");
                    return (ordered, "full-order+crops");
                }

                detail.AppendLine(
                    "full-order+crops produced little - try crop-primary / merge");
            }

            // Solid multi-balloon detect ? try crops first (wording + per-balloon order).
            // Also when crops clearly dominate a thin full-frame (full missed balloons),
            // even if detect looks scrapy - short real balloons must still be spoken.
            bool cropsDominateFull =
                cropWordsRaw >= fullWords + 6 &&
                cropReads.Count >= 2 &&
                fullWords < 24;
            bool cropPrimary =
                cropReads.Count >= 2 &&
                cropWordsRaw >= 8 &&
                (solidIslands || readingBlocks >= 3 || cropReads.Count >= 3 || cropsDominateFull) &&
                (!scrapDetect || cropsDominateFull);

            if (cropPrimary)
            {
                detail.AppendLine(
                    $"strategy=crop-primary (scrap={scrapDetect} solid={solidIslands} " +
                    $"blocks={readingBlocks} cropOk={cropReads.Count}" +
                    (cropsDominateFull ? " cropsDominate" : "") + ")");

                // Coverage from crop snips alone - gap-fill must not inflate this.
                bool cropsIncomplete =
                    fullWords >= cropWordsRaw + 8 ||
                    fullWords > (int)(cropWordsRaw * 1.35 + 0.5);

                string cropsJoined = string.Join(
                    " ", cropReads.Select(c => c.Text).Where(t => !SpeechCleaner.IsUnusableOcrText(t)));
                // One full blob + crops only partial token coverage ? prefer full path.
                if (!cropsIncomplete &&
                    fullUnitsProbe.Count == 1 &&
                    fullWords >= 16)
                {
                    double cover = SpeechCleaner.TokenCoverageOfAByB(fullUnitsProbe[0], cropsJoined);
                    if (cover < 0.72)
                    {
                        cropsIncomplete = true;
                        detail.AppendLine(
                            $"crop-primary monoblock full cover={cover:F2}<0.72 " +
                            $"(cropWords={cropWordsRaw} fullWords={fullWords})");
                    }
                }

                if (cropsIncomplete)
                {
                    detail.AppendLine(
                        $"crop-primary incomplete snips vs full " +
                        $"(cropWords={cropWordsRaw} fullWords={fullWords}) - " +
                        "fall back to full+crop-merge");
                }
                else
                {
                    var primary = BuildCropPrimarySpeakUnits(fullParts, cropReads, detail);
                    int primaryWords = primary.Sum(ComicRegionGeometry.CountWords);
                    if (primary.Count > 0 && primaryWords >= 4)
                    {
                        // Safety: if build mostly re-appended full as one mega unit,
                        // reject (double-speak path).
                        bool gapDump =
                            primary.Count >= 2 &&
                            fullUnitsProbe.Count == 1 &&
                            primary.Any(u =>
                                ComicRegionGeometry.CountWords(u) >= fullWords - 2 &&
                                SpeechCleaner.TokenCoverageOfAByB(fullUnitsProbe[0], u) >= 0.85);

                        // Prefer full when it covers nearly all crop speech but crops
                        // omit real full-frame content (mega-crop truncations, missed
                        // banners, "and it was", "next:"). Word-count alone lies when
                        // crops duplicate balloons.
                        string fullJoinedGate = string.Join(
                            " ", fullParts.Where(p => !SpeechCleaner.IsUnusableOcrText(p)));
                        string cropJoinedGate = string.Join(
                            " ", primary.Where(p => !SpeechCleaner.IsUnusableOcrText(p)));
                        double cropsInFull = SpeechCleaner.TokenCoverageOfAByB(
                            cropJoinedGate, fullJoinedGate);
                        double fullInCrops = SpeechCleaner.TokenCoverageOfAByB(
                            fullJoinedGate, cropJoinedGate);
                        // len>=3 catches "was"/"next"; len>=4 alone missed short captions
                        int novelInFull = CountContentTokensOnlyInA(
                            fullJoinedGate, cropJoinedGate, minLen: 3);
                        int novelInFullStrong = CountContentTokensOnlyInA(
                            fullJoinedGate, cropJoinedGate, minLen: 4);
                        int qFull = SpeechCleaner.OcrTextQualityScore(fullJoinedGate);
                        int qCrop = SpeechCleaner.OcrTextQualityScore(cropJoinedGate);
                        bool fullMoreComplete =
                            !gapDump &&
                            fullWords >= 10 &&
                            cropsInFull >= 0.80 &&
                            (
                                // Any real omission while crops sit inside full
                                (fullInCrops < 0.94 && novelInFull >= 1) ||
                                (fullInCrops < 0.97 && novelInFull >= 2) ||
                                (fullInCrops < 0.99 && novelInFullStrong >= 2) ||
                                (novelInFull >= 2 && qFull + 2 >= qCrop)
                            );

                        if (gapDump)
                        {
                            detail.AppendLine(
                                "crop-primary rejected gap-full monoblock dump - " +
                                "fall back to full+crop-merge");
                        }
                        else if (fullMoreComplete)
                        {
                            detail.AppendLine(
                                "crop-primary rejected: full-frame more complete " +
                                $"(cropsInFull={cropsInFull:F2} fullInCrops={fullInCrops:F2} " +
                                $"novelInFull={novelInFull} fullWords={fullWords} " +
                                $"cropUnits={primary.Count}) - fall back to full+crop-merge");
                        }
                        else
                        {
                            detail.AppendLine(
                                $"winner=crops-primary units={primary.Count} " +
                                $"words={primaryWords} " +
                                $"(full had {fullWords} words / " +
                                $"{fullUnitsProbe.Count} units; snipWords={cropWordsRaw})");
                            return (primary, "crops-primary");
                        }
                    }
                    else
                    {
                        detail.AppendLine(
                            "crop-primary produced little - fall back to full+crop-merge");
                    }
                }
            }
            else
            {
                detail.AppendLine(
                    $"strategy=full-primary (scrap={scrapDetect} solid={solidIslands} " +
                    $"blocks={readingBlocks} cropOk={cropReads.Count})");
            }

            // Working speak units from full-frame (blank-line balloons)
            var units = SpeechCleaner.ExpandToSpeakUnits(fullParts);
            if (units.Count == 0)
            {
                var onlyCrops = cropReads
                    .OrderBy(c => c.RegionIndex)
                    .Select(c => c.Text)
                    .ToList();
                detail.AppendLine(
                    $"winner=crops words={cropWordsRaw} (full expanded empty)");
                return (onlyCrops, "crops");
            }

            string fullJoined = string.Join(" ", fullParts);
            var fullTok = SpeechCleaner.ToTokenSet(fullJoined);

            int repairs = 0;
            int novelInserts = 0;
            int novelWordTotal = 0;
            int skipped = 0;

            foreach (var crop in cropReads.OrderBy(c => c.RegionIndex))
            {
                var cropWords = SpeechCleaner.TokenizeWords(crop.Text);
                if (cropWords.Count == 0) continue;

                int novel = 0;
                foreach (string w in cropWords)
                {
                    if (!fullTok.Contains(SpeechCleaner.NormalizeToken(w)))
                        novel++;
                }

                double novelFrac = novel / (double)cropWords.Count;
                // Parent unit for insert position (best overlap with whole crop)
                int parentIdx = FindBestMatchingUnit(crop.Text, units, minOverlap: 0.28);

                var cropUnits = SpeechCleaner.ExpandToSpeakUnits(new List<string> { crop.Text });
                if (cropUnits.Count == 0)
                    cropUnits.Add(crop.Text.Trim());

                bool anyAction = false;
                foreach (string cu in cropUnits)
                {
                    if (SpeechCleaner.IsUnusableOcrText(cu)) continue;

                    int matchIdx = FindBestMatchingUnit(cu, units, minOverlap: 0.40);
                    double ov = matchIdx >= 0
                        ? SpeechCleaner.TokenOverlapRatio(cu, units[matchIdx])
                        : 0;

                    // Repair: same balloon, crop wording clearly better
                    // (or equal quality but crop preferred when scores close + more alnum)
                    if (matchIdx >= 0 && ov >= 0.40 &&
                        (SpeechCleaner.IsClearlyBetterOcr(cu, units[matchIdx]) ||
                         PreferCropWording(cu, units[matchIdx])))
                    {
                        detail.AppendLine(
                            $"  crop-repair r{crop.RegionIndex + 1} unit[{matchIdx + 1}] " +
                            $"ov={ov:F2} \"{Truncate(units[matchIdx], 36)}\" ? " +
                            $"\"{Truncate(cu, 36)}\"");
                        units[matchIdx] = cu;
                        repairs++;
                        anyAction = true;
                        parentIdx = matchIdx;
                        continue;
                    }

                    // Novel unit / span ? insert after parent (reading order)
                    CountNovelTokens(cu, fullTok, out int uNovel, out int uTotal);
                    double uFrac = uTotal > 0 ? uNovel / (double)uTotal : 0;
                    bool unitNovel = IsNovelEnoughUnit(uNovel, uTotal, uFrac);
                    string? toInsert = null;
                    int insertNovelWords = 0;

                    if (unitNovel)
                    {
                        toInsert = cu;
                        insertNovelWords = uNovel;
                    }
                    else
                    {
                        // Mixed unit: try contiguous novel run inside
                        string? span = ExtractNovelCropSpans(cu, fullTok, out int spanNovel);
                        if (span != null && spanNovel >= 2 && !SpeechCleaner.IsUnusableOcrText(span))
                        {
                            toInsert = span;
                            insertNovelWords = spanNovel;
                        }
                    }

                    if (toInsert != null)
                    {
                        // Avoid inserting text already present after repair
                        if (units.Any(u => SpeechCleaner.TokenOverlapRatio(toInsert, u) >= 0.85))
                        {
                            detail.AppendLine(
                                $"  crop-novel skip-dup r{crop.RegionIndex + 1} " +
                                $"\"{Truncate(toInsert, 40)}\"");
                            continue;
                        }

                        if (!IsUsableNovelCropInsert(toInsert))
                        {
                            detail.AppendLine(
                                $"  crop-novel skip-weak r{crop.RegionIndex + 1} " +
                                $"\"{Truncate(toInsert, 40)}\"");
                            continue;
                        }

                        int insertAt = parentIdx >= 0 ? parentIdx + 1 : units.Count;
                        insertAt = Math.Clamp(insertAt, 0, units.Count);
                        units.Insert(insertAt, toInsert);
                        detail.AppendLine(
                            $"  crop-novel insert r{crop.RegionIndex + 1} after " +
                            $"{(parentIdx >= 0 ? parentIdx + 1 : 0)} " +
                            $"novel={insertNovelWords} \"{Truncate(toInsert, 40)}\"");
                        novelInserts++;
                        novelWordTotal += insertNovelWords;
                        anyAction = true;
                        parentIdx = insertAt;
                        continue;
                    }
                }

                // Whole-crop mostly novel and nothing applied yet
                if (!anyAction)
                {
                    bool mostlyNovel =
                        novelFrac >= 0.55 ||
                        (cropWords.Count >= 3 && novel >= cropWords.Count - 1);

                    if ((mostlyNovel && novel >= 2) ||
                        (fullWords < 8 && novel >= 2))
                    {
                        if (!IsUsableNovelCropInsert(crop.Text))
                        {
                            detail.AppendLine(
                                $"  crop-novel skip-weak-full r{crop.RegionIndex + 1} " +
                                $"\"{Truncate(crop.Text, 40)}\"");
                        }
                        else if (!units.Any(u => SpeechCleaner.TokenOverlapRatio(crop.Text, u) >= 0.85))
                        {
                            int insertAt = parentIdx >= 0 ? parentIdx + 1 : units.Count;
                            insertAt = Math.Clamp(insertAt, 0, units.Count);
                            units.Insert(insertAt, crop.Text);
                            detail.AppendLine(
                                $"  crop-novel insert-full r{crop.RegionIndex + 1} after " +
                                $"{(parentIdx >= 0 ? parentIdx + 1 : 0)} " +
                                $"novel={novel}/{cropWords.Count} \"{Truncate(crop.Text, 40)}\"");
                            novelInserts++;
                            novelWordTotal += novel;
                            anyAction = true;
                        }
                    }
                }

                if (!anyAction)
                {
                    skipped++;
                    detail.AppendLine(
                        $"  crop-subset skip r{crop.RegionIndex + 1} " +
                        $"novel={novel}/{cropWords.Count} \"{Truncate(crop.Text, 48)}\"");
                }
            }

            detail.AppendLine(
                $"best-of: fullWords={fullWords} cropWords={cropWordsRaw} " +
                $"repairs={repairs} novelInserts={novelInserts} " +
                $"novelWords={novelWordTotal} skipped={skipped} units={units.Count}");

            if (repairs > 0 || novelInserts > 0)
            {
                detail.AppendLine(
                    $"winner=full+crop-merge units={units.Count} " +
                    $"repairs={repairs} novelInserts={novelInserts}");
                return (units, "full+crop-merge");
            }

            // No crop contribution - pure full
            if (fullWords >= 12)
            {
                detail.AppendLine(
                    $"winner=full-frame words={fullWords} " +
                    $"(crops no repair/novel; skipped={skipped})");
                return (fullParts, "full-frame");
            }

            if (cropWordsRaw > fullWords + 8)
            {
                var onlyCrops = cropReads
                    .OrderBy(c => c.RegionIndex)
                    .Select(c => c.Text)
                    .ToList();
                detail.AppendLine(
                    $"winner=crops words={cropWordsRaw} (clearly longer than full {fullWords})");
                return (onlyCrops, "crops");
            }

            detail.AppendLine(
                $"winner=full-frame words={fullWords} (default; crops skipped={skipped})");
            return (fullParts, "full-frame");
        }

        /// <summary>
        /// Phase B: speak in <b>full-frame balloon order</b>; replace a unit with
        /// crop wording when the snip matches and is equal/better OCR. Full units
        /// with no crop match are kept (missed islands). Unused novel crop snips
        /// may insert after their best parent.
        /// </summary>
        public static List<string> BuildFullOrderCropWordingSpeakUnits(
            List<string> fullParts,
            List<CropRead> cropReads,
            StringBuilder detail)
        {
            var fullUnits = SpeechCleaner.ExpandToSpeakUnits(fullParts);
            if (fullUnits.Count == 0)
                return new List<string>();

            var snips = new List<(string Text, int RegionIndex)>();
            foreach (var crop in cropReads.OrderBy(c => c.RegionIndex))
            {
                if (SpeechCleaner.IsUnusableOcrText(crop.Text))
                    continue;
                var parts = SpeechCleaner.ExpandToSpeakUnits(new List<string> { crop.Text });
                if (parts.Count == 0)
                    parts.Add(crop.Text.Trim());
                foreach (string p in parts)
                {
                    if (!SpeechCleaner.IsUnusableOcrText(p) && ComicRegionGeometry.CountWords(p) >= MinSpeakUnitWords)
                        snips.Add((p, crop.RegionIndex));
                }
            }

            var usedSnip = new bool[snips.Count];
            var result = new List<string>();
            int cropWords = 0;
            int fullKeeps = 0;
            int gaps = 0;

            for (int fi = 0; fi < fullUnits.Count; fi++)
            {
                string fu = fullUnits[fi];
                if (SpeechCleaner.IsUnusableOcrText(fu) || ComicRegionGeometry.CountWords(fu) < MinSpeakUnitWords)
                    continue;

                int fw = ComicRegionGeometry.CountWords(fu);
                int bestSi = -1;
                double bestScore = 0;

                for (int si = 0; si < snips.Count; si++)
                {
                    if (usedSnip[si])
                        continue;

                    string sn = snips[si].Text;
                    int sw = ComicRegionGeometry.CountWords(sn);
                    double ov = SpeechCleaner.TokenOverlapRatio(fu, sn);
                    double fullInSnip = SpeechCleaner.TokenCoverageOfAByB(fu, sn);
                    double snipInFull = SpeechCleaner.TokenCoverageOfAByB(sn, fu);
                    double score = Math.Max(ov, Math.Max(fullInSnip * 0.95, snipInFull * 0.90));

                    // Mega snip that dumps half the page must not steal every balloon.
                    if (fw >= 3 && sw >= fw * 2 + 6)
                        score *= 0.45;
                    // Tiny scrap of a long balloon
                    if (fw >= 8 && sw <= 3 && fullInSnip < 0.35)
                        score *= 0.5;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestSi = si;
                    }
                }

                if (bestSi < 0 || bestScore < 0.36)
                {
                    result.Add(fu);
                    gaps++;
                    fullKeeps++;
                    detail.AppendLine(
                        $"  full-order gap-full[{fi + 1}] \"{Truncate(fu, 48)}\"");
                    continue;
                }

                string snip = snips[bestSi].Text;
                int swN = ComicRegionGeometry.CountWords(snip);
                usedSnip[bestSi] = true;

                // Partial / truncated snip ? always keep full (order from full, wording from full)
                double coverFull = SpeechCleaner.TokenCoverageOfAByB(fu, snip);
                double snipCoveredByFull = SpeechCleaner.TokenCoverageOfAByB(snip, fu);
                if (swN + 2 < fw || coverFull < 0.82)
                {
                    result.Add(fu);
                    fullKeeps++;
                    detail.AppendLine(
                        $"  full-order keep-full[{fi + 1}] snip-incomplete " +
                        $"score={bestScore:F2} cover={coverFull:F2} " +
                        $"\"{Truncate(fu, 40)}\"");
                    // Release incomplete snip so it cannot lock better matches
                    usedSnip[bestSi] = false;
                    continue;
                }

                // Prefer crop when it covers this full unit and is equal/better OCR.
                bool preferCrop =
                    coverFull >= 0.88 &&
                    swN >= fw - 2 &&
                    snipCoveredByFull >= 0.75 &&
                    (SpeechCleaner.IsClearlyBetterOcr(snip, fu) ||
                     PreferCropWording(snip, fu) ||
                     string.Equals(
                         ComicConsensus.NormalizeOcrCompare(snip),
                         ComicConsensus.NormalizeOcrCompare(fu),
                         StringComparison.OrdinalIgnoreCase));

                if (preferCrop)
                {
                    result.Add(snip);
                    cropWords++;
                    detail.AppendLine(
                        $"  full-order crop-word[{fi + 1}] r{snips[bestSi].RegionIndex + 1} " +
                        $"score={bestScore:F2}" +
                        $" \"{Truncate(snip, 44)}\"");
                }
                else
                {
                    result.Add(fu);
                    fullKeeps++;
                    detail.AppendLine(
                        $"  full-order keep-full[{fi + 1}] score={bestScore:F2} " +
                        $"\"{Truncate(fu, 44)}\"");
                }
            }

            // Novel inserts: only when full sequence is thin. Solid multi-balloon full
            // already has the story; crop novels from over-split islands are usually junk.
            // Strict gates: reject truncated crop mush that paraphrases full.
            int novels = 0;
            string fullJoinForNovel = string.Join(" ", result);
            if (fullUnits.Count < 5)
            {
                for (int si = 0; si < snips.Count; si++)
                {
                    if (usedSnip[si])
                        continue;
                    string sn = snips[si].Text;
                    if (!IsUsableNovelCropInsert(sn))
                    {
                        detail.AppendLine(
                            $"  full-order novel-skip-weak r{snips[si].RegionIndex + 1} " +
                            $"\"{Truncate(sn, 40)}\"");
                        continue;
                    }

                    if (LooksLikeTruncatedOcrGarbage(sn, fullJoinForNovel))
                    {
                        detail.AppendLine(
                            $"  full-order novel-skip-trunc r{snips[si].RegionIndex + 1} " +
                            $"\"{Truncate(sn, 40)}\"");
                        continue;
                    }

                    // Coverage vs any single unit *and* the whole spoken join.
                    // Caption scraps often sit across split full units (low per-unit
                    // cover) but are already fully present in the join — do not re-insert.
                    double alreadyUnit = 0;
                    foreach (string u in result)
                        alreadyUnit = Math.Max(alreadyUnit, SpeechCleaner.TokenCoverageOfAByB(sn, u));
                    double alreadyJoin = SpeechCleaner.TokenCoverageOfAByB(sn, fullJoinForNovel);
                    double already = Math.Max(alreadyUnit, alreadyJoin);

                    var spokenTok = SpeechCleaner.ToTokenSet(fullJoinForNovel);
                    CountNovelContentTokens(sn, spokenTok, out int novelContent, out int contentTotal);

                    // Near-total echo / weak restatement of spoken text → skip.
                    // Partial stopword overlap alone must not kill a real missed balloon
                    // ("the cold" vs unit containing "there…").
                    bool mostlyEcho =
                        already >= 0.78 ||
                        (alreadyJoin >= 0.55 && novelContent < 3) ||
                        (already >= 0.62 && novelContent < 2) ||
                        (already >= 0.45 && novelContent == 0 && contentTotal >= 2);
                    if (mostlyEcho)
                    {
                        detail.AppendLine(
                            $"  full-order novel-skip-covered r{snips[si].RegionIndex + 1} " +
                            $"cover={already:F2} join={alreadyJoin:F2} " +
                            $"novelContent={novelContent} \"{Truncate(sn, 40)}\"");
                        continue;
                    }

                    // Weaker / shorter paraphrase of something already spoken
                    if (contentTotal >= 4 &&
                        novelContent <= 1 &&
                        alreadyJoin >= 0.40)
                    {
                        detail.AppendLine(
                            $"  full-order novel-skip-weaker r{snips[si].RegionIndex + 1} " +
                            $"join={alreadyJoin:F2} novelContent={novelContent} " +
                            $"\"{Truncate(sn, 40)}\"");
                        continue;
                    }

                    if (novelContent >= 2)
                    {
                        detail.AppendLine(
                            $"  full-order novel-keep-missed r{snips[si].RegionIndex + 1} " +
                            $"cover={already:F2} join={alreadyJoin:F2} " +
                            $"novelContent={novelContent} \"{Truncate(sn, 40)}\"");
                    }

                    int qSn = SpeechCleaner.OcrTextQualityScore(sn);
                    if (ComicRegionGeometry.CountWords(sn) >= 5 && qSn < 28)
                    {
                        detail.AppendLine(
                            $"  full-order novel-skip-q r{snips[si].RegionIndex + 1} " +
                            $"q={qSn} \"{Truncate(sn, 40)}\"");
                        continue;
                    }

                    // Truncated caption scrap: ends mid-word / no terminal punct and
                    // shorter than a better full unit that already covers most of it.
                    if (LooksLikeWeakCaptionScrap(sn, result, alreadyJoin))
                    {
                        detail.AppendLine(
                            $"  full-order novel-skip-scrap r{snips[si].RegionIndex + 1} " +
                            $"\"{Truncate(sn, 40)}\"");
                        continue;
                    }

                    int parent = FindBestMatchingUnit(sn, result, minOverlap: 0.18);
                    int insertAt;
                    if (parent >= 0)
                    {
                        insertAt = parent + 1;
                    }
                    else
                    {
                        // No full-frame parent (missed column): place by region rank
                        // so right-top balloons land before the bottom caption.
                        int maxR = 0;
                        for (int j = 0; j < snips.Count; j++)
                            if (snips[j].RegionIndex > maxR)
                                maxR = snips[j].RegionIndex;
                        double frac = maxR <= 0
                            ? 1.0
                            : (snips[si].RegionIndex + 0.5) / (maxR + 1.0);
                        insertAt = (int)Math.Round(frac * result.Count);
                    }
                    insertAt = Math.Clamp(insertAt, 0, result.Count);
                    result.Insert(insertAt, sn);
                    fullJoinForNovel = string.Join(" ", result);
                    novels++;
                    detail.AppendLine(
                        $"  full-order novel-insert @{insertAt + 1} " +
                        $"r{snips[si].RegionIndex + 1} \"{Truncate(sn, 44)}\"");
                }
            }
            else
            {
                detail.AppendLine(
                    $"  full-order novel-skip (fullUnits={fullUnits.Count}=5 - trust full order)");
            }

            detail.AppendLine(
                $"full-order summary: units={result.Count} cropWord={cropWords} " +
                $"fullKeep={fullKeeps} gaps={gaps} novel={novels}");
            return result;
        }

        /// <summary>
        /// Crop-led speak list ordered by <b>geometry</b> (reading-block region rank
        /// + within-crop blank-line order). Full-frame supplies gap-fills only -
        /// never reorders crop wording via fuzzy full-unit matching.
        /// Full almost never overwrites a matching crop (strict margin).
        /// </summary>
        public static List<string> BuildCropPrimarySpeakUnits(
            List<string> fullParts,
            List<CropRead> cropReads,
            StringBuilder detail)
        {
            var fullUnits = SpeechCleaner.ExpandToSpeakUnits(fullParts);

            // (orderKey, tie, text, tag)
            // RegionIndex is the reading-block list order (SortComicReadingOrder).
            // Stride keeps every unit of region R before every unit of region R+1.
            const double RegionOrderStride = 1000.0;
            var items = new List<(double Order, int Tie, string Text, string Tag)>();
            int tie = 0;
            var cropSnips = new List<(string Text, double Order)>();
            int cropKept = 0;

            foreach (var crop in cropReads.OrderBy(c => c.RegionIndex))
            {
                var cropUnits = SpeechCleaner.ExpandToSpeakUnits(new List<string> { crop.Text });
                if (cropUnits.Count == 0 && !SpeechCleaner.IsUnusableOcrText(crop.Text))
                    cropUnits.Add(crop.Text.Trim());

                int sub = 0;
                foreach (string cu in cropUnits)
                {
                    if (SpeechCleaner.IsUnusableOcrText(cu))
                        continue;

                    // Geometry-primary: region rank + dump order inside the crop.
                    // Do not map onto full-frame unit index (full order is often wrong
                    // and fuzzy matches collide / invent mid-stack slots).
                    double order = crop.RegionIndex * RegionOrderStride + sub;
                    sub++;

                    items.Add((order, tie++, cu, $"r{crop.RegionIndex + 1}"));
                    cropSnips.Add((cu, order));
                    cropKept++;
                    detail.AppendLine(
                        $"  crop-primary keep r{crop.RegionIndex + 1} " +
                        $"order={order:F2} \"{Truncate(cu, 48)}\"");
                }
            }

            // Union of all crop snip text - for partial coverage of monoblock full.
            string cropsUnionText = string.Join(" ", cropSnips.Select(c => c.Text));
            var cropTok = SpeechCleaner.ToTokenSet(cropsUnionText);

            // Which full units are already covered by crop snips ? that snip's order
            var coveredFullOrder = new Dictionary<int, double>();
            // Residuals: full unit text + char position so we can order by story
            // position (and it was before next:), not only by max crop order.
            var residualFromFull =
                new List<(int FullIndex, string FullUnit, string Text, int CharPos)>();

            for (int fi = 0; fi < fullUnits.Count; fi++)
            {
                string fu = fullUnits[fi];
                if (SpeechCleaner.IsUnusableOcrText(fu) || ComicRegionGeometry.CountWords(fu) < MinSpeakUnitWords)
                    continue;

                double bestOv = 0;
                double bestOrder = 0;
                foreach (var (text, order) in cropSnips)
                {
                    double ov = SpeechCleaner.TokenOverlapRatio(fu, text);
                    if (ov > bestOv)
                    {
                        bestOv = ov;
                        bestOrder = order;
                    }
                }

                // Also: how much of this full unit is present across ALL snips combined
                // (monoblock full vs several partial crops).
                double unionCover = SpeechCleaner.TokenCoverageOfAByB(fu, cropsUnionText);

                if (bestOv >= 0.42)
                {
                    coveredFullOrder[fi] = bestOrder;
                    // Still pull residual novel tails from a long full unit only
                    // partially matched by one snip (rare).
                    if (ComicRegionGeometry.CountWords(fu) >= 16 && unionCover < 0.97 && unionCover >= 0.40)
                    {
                        foreach (var (res, pos) in ExtractResidualFullSpans(fu, cropTok))
                            residualFromFull.Add((fi, fu, res, pos));
                    }
                }
                else if (unionCover >= 0.55)
                {
                    // Partially covered by snips collectively - do not re-speak whole
                    // monoblock, but insert residual novel captions/tails.
                    double anchor = cropSnips.Count > 0
                        ? cropSnips.Max(c => c.Order)
                        : 0;
                    coveredFullOrder[fi] = anchor;
                    detail.AppendLine(
                        $"  crop-primary full[{fi + 1}] covered-by-snip-union " +
                        $"cover={unionCover:F2} (residual-scan, skip whole gap-full)");

                    if (unionCover < 0.995)
                    {
                        foreach (var (res, pos) in ExtractResidualFullSpans(fu, cropTok))
                            residualFromFull.Add((fi, fu, res, pos));
                    }
                }
            }

            // Gap-fill: full balloons crops missed. Slot between neighboring
            // covered full units by story index (anchor to crop geometry orders).
            // Never append a monoblock full that only partially overlaps crops
            // (that re-reads the whole panel after partial snips).
            int gaps = 0;
            double cropsEnd = cropSnips.Count > 0
                ? cropSnips.Max(c => c.Order) + 1.0
                : 0.0;

            for (int fi = 0; fi < fullUnits.Count; fi++)
            {
                string fu = fullUnits[fi];
                if (SpeechCleaner.IsUnusableOcrText(fu) || ComicRegionGeometry.CountWords(fu) < MinSpeakUnitWords)
                    continue;
                if (coveredFullOrder.ContainsKey(fi))
                    continue;

                double unionCover = SpeechCleaner.TokenCoverageOfAByB(fu, cropsUnionText);
                int fuWords = ComicRegionGeometry.CountWords(fu);

                // Monoblock / fat full unit only partly in crops ? residual only,
                // not the whole unit (avoids double-speaking the panel).
                if (cropSnips.Count > 0 &&
                    fuWords >= 12 &&
                    unionCover > 0.18 &&
                    unionCover < 0.88)
                {
                    detail.AppendLine(
                        $"  crop-primary skip gap-full[{fi + 1}] partial monoblock " +
                        $"cover={unionCover:F2} words={fuWords}");
                    foreach (var (res, pos) in ExtractResidualFullSpans(fu, cropTok))
                        residualFromFull.Add((fi, fu, res, pos));
                    continue;
                }

                // Tiny residual already almost entirely in snips ? still scan tails
                if (unionCover >= 0.80)
                {
                    detail.AppendLine(
                        $"  crop-primary skip gap-full[{fi + 1}] already in snips " +
                        $"cover={unionCover:F2}");
                    if (unionCover < 0.995)
                    {
                        foreach (var (res, pos) in ExtractResidualFullSpans(fu, cropTok))
                            residualFromFull.Add((fi, fu, res, pos));
                    }
                    continue;
                }

                double? before = null;
                double? after = null;
                foreach (var kv in coveredFullOrder)
                {
                    if (kv.Key < fi)
                        before = before.HasValue ? Math.Max(before.Value, kv.Value) : kv.Value;
                    else if (kv.Key > fi)
                        after = after.HasValue ? Math.Min(after.Value, kv.Value) : kv.Value;
                }

                double order;
                if (before.HasValue && after.HasValue && after.Value > before.Value)
                    order = (before.Value + after.Value) / 2.0;
                else if (before.HasValue)
                    order = before.Value + 0.5; // after preceding sibling content
                else if (after.HasValue)
                    order = after.Value - 0.5;
                else
                    order = cropsEnd + gaps; // no anchor - append after all crops

                items.Add((order, tie++, fu, "gap-full"));
                gaps++;
                detail.AppendLine(
                    $"  crop-primary gap-full order={order:F2} \"{Truncate(fu, 48)}\"");
            }

            // Insert residual novel spans from full (and it was / next: …) ordered by
            // their position in the full-frame text and neighboring crop snips
            // (so "and it was" lands after beginning, before next: first friends).
            var residualSorted = residualFromFull
                .OrderBy(r => r.FullIndex)
                .ThenBy(r => r.CharPos < 0 ? int.MaxValue : r.CharPos)
                .ThenBy(r => r.Text, StringComparer.Ordinal)
                .ToList();

            int residualAdds = 0;
            foreach (var (fi, fullUnit, res, charPos) in residualSorted)
            {
                if (SpeechCleaner.IsUnusableOcrText(res) || ComicRegionGeometry.CountWords(res) < 1)
                    continue;
                // Skip if residual is already essentially in crop snips / items
                string existing = string.Join(
                    " ",
                    items.Select(it => it.Text).Concat(cropSnips.Select(c => c.Text)));
                if (SpeechCleaner.TokenCoverageOfAByB(res, existing) >= 0.85)
                    continue;

                int pos = charPos >= 0
                    ? charPos
                    : IndexOfSpeakableInText(fullUnit, res);
                double order = OrderResidualAmongCrops(
                    fullUnit, pos, res, cropSnips, cropsEnd);
                // Stable secondary so two residuals at same slot keep story order
                order += residualAdds * 0.001;
                items.Add((order, tie++, res, "gap-residual"));
                residualAdds++;
                gaps++;
                detail.AppendLine(
                    $"  crop-primary gap-residual full[{fi + 1}] " +
                    $"pos={pos} order={order:F2} \"{Truncate(res, 48)}\"");
            }

            items.Sort((a, b) =>
            {
                int cmp = a.Order.CompareTo(b.Order);
                return cmp != 0 ? cmp : a.Tie.CompareTo(b.Tie);
            });

            // Build final list; dedupe high-overlap; crop wording wins ties
            var units = new List<string>();
            int fullOverwrite = 0;
            foreach (var item in items)
            {
                int dup = FindBestMatchingUnit(item.Text, units, minOverlap: 0.78);
                if (dup >= 0)
                {
                    bool incomingIsCrop = item.Tag.StartsWith("r", StringComparison.Ordinal);
                    bool keepIncoming =
                        SpeechCleaner.IsClearlyBetterOcr(item.Text, units[dup]) ||
                        (incomingIsCrop && PreferCropWording(item.Text, units[dup])) ||
                        // Never let a gap-full replace a crop unless full crushes it
                        (!incomingIsCrop && FullClearlyBeatsCrop(item.Text, units[dup]));

                    if (keepIncoming)
                    {
                        detail.AppendLine(
                            $"  crop-primary replace-dup {item.Tag} " +
                            $"\"{Truncate(units[dup], 36)}\" ? \"{Truncate(item.Text, 36)}\"");
                        if (!incomingIsCrop)
                            fullOverwrite++;
                        units[dup] = item.Text;
                    }
                    else
                    {
                        detail.AppendLine(
                            $"  crop-primary skip-dup {item.Tag} " +
                            $"\"{Truncate(item.Text, 40)}\"");
                    }
                    continue;
                }

                units.Add(item.Text);
            }

            detail.AppendLine(
                $"crop-primary summary: cropSnips={cropKept} gapFills={gaps} " +
                $"fullOverwrite={fullOverwrite} finalUnits={units.Count}");
            return units;
        }

        // -----------------------------------------------------------------------
        // Geometry-guided full-frame balloon split
        // -----------------------------------------------------------------------

        /// <summary>
        /// When full-frame VL under-splits (monoblock) but WinOCR detect has multiple
        /// reading islands, cut the full transcript using region text as anchors.
        /// Each region is matched independently in the full string (region list order
        /// need not match text order); overlaps are resolved by score then position.
        /// Returns null to keep the original fullParts (fail-closed).
        /// Does not invent wording — only slices existing full-frame text.
        /// </summary>
        public static List<string>? SplitFullFrameByDetectRegions(
            List<string> fullParts,
            List<DetectedTextRegion> regions,
            StringBuilder detail)
        {
            if (fullParts == null || fullParts.Count == 0 ||
                regions == null || regions.Count < 2)
                return null;

            var existingUnits = SpeechCleaner.ExpandToSpeakUnits(fullParts);
            int fullWords = fullParts.Sum(ComicRegionGeometry.CountWords);
            if (fullWords < 12)
            {
                detail.AppendLine(
                    $"full-split skip: fullWords={fullWords}<12");
                return null;
            }

            // Already well split relative to detect — leave alone
            if (existingUnits.Count >= 2 &&
                existingUnits.Count * 2 >= regions.Count)
            {
                detail.AppendLine(
                    $"full-split skip: already split units={existingUnits.Count} " +
                    $"regions={regions.Count}");
                return null;
            }

            string fullJoined = string.Join("\n\n", fullParts.Where(p => !SpeechCleaner.IsUnusableOcrText(p)));
            if (SpeechCleaner.IsUnusableOcrText(fullJoined))
                return null;

            string hay = fullJoined.Replace("\r\n", "\n").Replace('\r', '\n');

            var anchors = new List<(int RegionIndex, List<string> Tokens)>();
            for (int i = 0; i < regions.Count; i++)
            {
                var toks = BuildRegionAnchorTokens(regions[i].WinOcrText);
                if (toks.Count > 0)
                    anchors.Add((i, toks));
                else
                    detail.AppendLine(
                        $"  full-split r{i + 1}: no anchor tokens " +
                        $"(winocr=\"{Truncate(regions[i].WinOcrText, 40)}\")");
            }

            if (anchors.Count < 2)
            {
                detail.AppendLine(
                    $"full-split skip: usable anchors={anchors.Count}<2");
                return null;
            }

            // Independent best hit per region anywhere in full text (no forward cursor)
            var candidates = new List<(int RegionIndex, int CharPos, int MatchLen, double Score)>();
            foreach (var (ri, toks) in anchors)
            {
                var hit = FindBestAnchorInFull(hay, toks);
                if (hit.CharPos < 0)
                {
                    detail.AppendLine(
                        $"  full-split r{ri + 1}: miss tokens=[{string.Join(" ", toks.Take(6))}]");
                    continue;
                }

                candidates.Add((ri, hit.CharPos, hit.MatchLen, hit.Score));
                detail.AppendLine(
                    $"  full-split r{ri + 1}: candidate pos={hit.CharPos} " +
                    $"score={hit.Score:F2} tokens=[{string.Join(" ", toks.Take(5))}]");
            }

            if (candidates.Count < 2)
            {
                detail.AppendLine(
                    $"full-split abort: candidates={candidates.Count}<2");
                return null;
            }

            // Resolve overlaps: keep higher score (then earlier pos); drop losers
            var hits = ResolveNonOverlappingAnchorHits(candidates, detail);
            if (hits.Count < 2)
            {
                detail.AppendLine(
                    $"full-split abort: non-overlap hits={hits.Count}<2");
                return null;
            }

            hits = hits
                .OrderBy(h => h.CharPos)
                .ThenBy(h => h.RegionIndex)
                .ToList();

            for (int i = 1; i < hits.Count; i++)
            {
                if (hits[i].CharPos <= hits[i - 1].CharPos)
                {
                    detail.AppendLine(
                        $"full-split abort: non-increasing cuts " +
                        $"{hits[i - 1].CharPos} ? {hits[i].CharPos}");
                    return null;
                }
            }

            // Slice between consecutive hits. Prefix before first hit is its own unit
            // when it has real words (earlier balloon that had a weak/missing anchor).
            var units = new List<string>();
            for (int i = 0; i < hits.Count; i++)
            {
                int start = hits[i].CharPos;
                int end = i + 1 < hits.Count ? hits[i + 1].CharPos : hay.Length;

                if (i == 0 && start > 0)
                {
                    string prefix = CollapseInternalWhitespace(hay[..start]);
                    // Keep short prefixes as their own unit (scrap filter off).
                    if (!SpeechCleaner.IsUnusableOcrText(prefix) &&
                        ComicRegionGeometry.CountWords(prefix) >= MinSpeakUnitWords)
                    {
                        units.Add(prefix);
                        detail.AppendLine(
                            $"  full-split prefix-unit words={ComicRegionGeometry.CountWords(prefix)} " +
                            $"\"{Truncate(prefix, 48)}\"");
                    }
                    else if (!string.IsNullOrWhiteSpace(prefix))
                    {
                        // Unusable alone → glue onto first body so it is not lost.
                        string body0 = CollapseInternalWhitespace(hay[start..end]);
                        string glued = string.IsNullOrEmpty(body0)
                            ? prefix
                            : prefix + " " + body0;
                        if (!SpeechCleaner.IsUnusableOcrText(glued))
                            units.Add(glued);
                        continue;
                    }
                }

                string body = CollapseInternalWhitespace(hay[start..end]);
                if (!SpeechCleaner.IsUnusableOcrText(body) && ComicRegionGeometry.CountWords(body) >= 1)
                    units.Add(body);
            }

            if (units.Count < 2)
            {
                detail.AppendLine(
                    $"full-split abort: produced units={units.Count}<2");
                return null;
            }

            if (!ValidateFullFrameSplit(hay, units, regions.Count, detail))
                return null;

            detail.AppendLine(
                $"full-split ok: units={units.Count} hits={hits.Count}/" +
                $"{anchors.Count} regions={regions.Count} " +
                $"words={units.Sum(ComicRegionGeometry.CountWords)}");
            for (int u = 0; u < units.Count; u++)
            {
                detail.AppendLine(
                    $"  full-split unit[{u + 1}/{units.Count}]: {Truncate(units[u], 56)}");
            }

            return units;
        }

        /// <summary>
        /// Greedy non-overlap: higher score wins, then earlier position.
        /// Intervals [pos, pos+span) that overlap more than a small slack drop the worse.
        /// </summary>
        public static List<(int RegionIndex, int CharPos, int MatchLen, double Score)>
            ResolveNonOverlappingAnchorHits(
                List<(int RegionIndex, int CharPos, int MatchLen, double Score)> candidates,
                StringBuilder detail)
        {
            var ordered = candidates
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.CharPos)
                .ThenBy(c => c.RegionIndex)
                .ToList();

            var kept = new List<(int RegionIndex, int CharPos, int MatchLen, double Score)>();
            foreach (var c in ordered)
            {
                int c0 = c.CharPos;
                int c1 = c.CharPos + Math.Max(8, c.MatchLen);
                bool overlaps = false;
                foreach (var k in kept)
                {
                    int k0 = k.CharPos;
                    int k1 = k.CharPos + Math.Max(8, k.MatchLen);
                    // Small slack so adjacent balloons can sit close
                    int slack = 12;
                    if (c0 < k1 - slack && k0 < c1 - slack)
                    {
                        overlaps = true;
                        detail.AppendLine(
                            $"  full-split drop r{c.RegionIndex + 1} pos={c.CharPos} " +
                            $"(overlap r{k.RegionIndex + 1} pos={k.CharPos} " +
                            $"score {c.Score:F2}<={k.Score:F2})");
                        break;
                    }
                }
                if (!overlaps)
                    kept.Add(c);
            }

            return kept;
        }

        /// <summary>Content tokens from WinOCR line text for anchoring in full-frame.</summary>
        public static List<string> BuildRegionAnchorTokens(string? winOcrText)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(winOcrText))
                return list;

            foreach (string w in SpeechCleaner.TokenizeWords(winOcrText))
            {
                string n = SpeechCleaner.NormalizeToken(w);
                if (n.Length < 3)
                    continue;
                // Drop pure digit scraps and single-symbol noise
                if (n.All(char.IsDigit))
                    continue;
                list.Add(n);
                if (list.Count >= 10)
                    break;
            }

            return list;
        }

        public readonly struct AnchorHit
        {
            public int CharPos { get; init; }
            public int MatchLen { get; init; }
            public double Score { get; init; }
        }

        /// <summary>
        /// Best match of anchor tokens anywhere in <paramref name="hay"/>.
        /// Prefers higher token-hit score, then earlier character position
        /// (so "lesson two" at pos 0 beats a weaker mid-page partial).
        /// </summary>
        public static AnchorHit FindBestAnchorInFull(
            string hay, List<string> anchorTokens)
        {
            if (string.IsNullOrEmpty(hay) || anchorTokens.Count == 0)
                return new AnchorHit { CharPos = -1, MatchLen = 0, Score = 0 };

            var ordered = anchorTokens
                .OrderByDescending(t => t.Length)
                .ThenBy(t => t, StringComparer.Ordinal)
                .ToList();

            AnchorHit best = new() { CharPos = -1, MatchLen = 0, Score = -1 };
            int minHits = anchorTokens.Count >= 3 ? 2 : 1;

            foreach (string needle in ordered.Take(Math.Min(5, ordered.Count)))
            {
                int from = 0;
                while (from < hay.Length)
                {
                    int at = IndexOfTokenInText(hay, needle, from);
                    if (at < 0)
                        break;

                    int windowEnd = Math.Min(hay.Length, at + Math.Max(100, needle.Length * 14));
                    string window = hay[at..windowEnd];
                    int hitCount = 0;
                    int searchAt = 0;
                    foreach (string t in anchorTokens)
                    {
                        int p = IndexOfTokenInText(window, t, searchAt);
                        if (p < 0)
                            continue;
                        hitCount++;
                        searchAt = p + t.Length;
                    }

                    if (hitCount >= minHits)
                    {
                        double score = hitCount / (double)Math.Max(1, anchorTokens.Count);
                        // Slight bonus for longer needles / more absolute hits
                        score += hitCount * 0.02;
                        int matchLen = Math.Max(needle.Length, searchAt > 0 ? searchAt : needle.Length);
                        if (score > best.Score + 1e-6 ||
                            (Math.Abs(score - best.Score) < 1e-6 &&
                             (best.CharPos < 0 || at < best.CharPos)))
                        {
                            best = new AnchorHit
                            {
                                CharPos = at,
                                MatchLen = matchLen,
                                Score = score
                            };
                        }
                    }

                    from = at + 1;
                }
            }

            if (best.CharPos < 0)
                return new AnchorHit { CharPos = -1, MatchLen = 0, Score = 0 };
            return best;
        }

        /// <summary>
        /// Case-insensitive token search that prefers word-ish boundaries.
        /// </summary>
        public static int IndexOfTokenInText(string hay, string token, int start)
        {
            if (string.IsNullOrEmpty(hay) || string.IsNullOrEmpty(token))
                return -1;
            start = Math.Clamp(start, 0, hay.Length);

            int from = start;
            while (from < hay.Length)
            {
                int at = hay.IndexOf(token, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    return -1;

                bool leftOk = at == 0 || !char.IsLetterOrDigit(hay[at - 1]);
                int end = at + token.Length;
                bool rightOk = end >= hay.Length || !char.IsLetterOrDigit(hay[end]);
                if (leftOk && rightOk)
                    return at;

                from = at + 1;
            }

            return -1;
        }

        /// <summary>
        /// Fail-closed validation: split must preserve almost all full-frame content
        /// and not explode into noise units.
        /// </summary>
        public static bool ValidateFullFrameSplit(
            string originalFull,
            List<string> units,
            int regionCount,
            StringBuilder detail)
        {
            if (units.Count < 2)
            {
                detail.AppendLine("full-split validate fail: units<2");
                return false;
            }

            // Cap over-split: at most regionCount + 2 (prefix edge cases)
            if (units.Count > regionCount + 2)
            {
                detail.AppendLine(
                    $"full-split validate fail: units={units.Count}>regions+2={regionCount + 2}");
                return false;
            }

            string joined = string.Join(" ", units);
            double coverOrig = SpeechCleaner.TokenCoverageOfAByB(originalFull, joined);
            double coverJoin = SpeechCleaner.TokenCoverageOfAByB(joined, originalFull);
            if (coverOrig < 0.90)
            {
                detail.AppendLine(
                    $"full-split validate fail: coverOrig={coverOrig:F2}<0.90 " +
                    "(would drop full-frame words)");
                return false;
            }

            // Joined shouldn't be wildly larger than original (dup glue)
            if (coverJoin < 0.75 && ComicRegionGeometry.CountWords(joined) > ComicRegionGeometry.CountWords(originalFull) + 8)
            {
                detail.AppendLine(
                    $"full-split validate fail: coverJoin={coverJoin:F2} " +
                    $"joinedWords={ComicRegionGeometry.CountWords(joined)} fullWords={ComicRegionGeometry.CountWords(originalFull)}");
                return false;
            }

            int emptyish = units.Count(u => SpeechCleaner.IsUnusableOcrText(u) || ComicRegionGeometry.CountWords(u) < 1);
            if (emptyish > 0)
            {
                detail.AppendLine(
                    $"full-split validate fail: emptyish={emptyish}");
                return false;
            }

            detail.AppendLine(
                $"full-split validate ok: coverOrig={coverOrig:F2} " +
                $"coverJoin={coverJoin:F2} units={units.Count}");
            return true;
        }

        public static string CollapseInternalWhitespace(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "";
            // Keep intentional blank lines if any; squash single newlines to space
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            s = Regex.Replace(s, @"[^\S\n]+", " ");
            s = Regex.Replace(s, @" *\n *", "\n");
            s = Regex.Replace(s, @"\n+", m => m.Length >= 2 ? "\n\n" : " ");
            s = Regex.Replace(s, @" {2,}", " ");
            return s.Trim();
        }

        /// <summary>
        /// Full may replace crop only with a clear quality margin (not near-ties).
        /// Blocks money/month over myself-style thrashing.
        /// </summary>
        public static bool FullClearlyBeatsCrop(string fullUnit, string crop)
        {
            if (SpeechCleaner.IsUnusableOcrText(fullUnit) || SpeechCleaner.IsUnusableOcrText(crop))
                return false;
            if (PreferCropWording(crop, fullUnit))
                return false;

            int qf = SpeechCleaner.OcrTextQualityScore(fullUnit);
            int qc = SpeechCleaner.OcrTextQualityScore(crop);
            // Need a solid lead - not a 1-point OCR coin flip
            if (qf < qc + 5)
                return false;
            if (!SpeechCleaner.IsClearlyBetterOcr(fullUnit, crop))
                return false;
            // Prefer longer/more complete full only if it doesn't look garbled
            if (SpeechCleaner.CountAlnum(fullUnit) + 3 < SpeechCleaner.CountAlnum(crop))
                return false;
            return true;
        }

        /// <summary>
        /// Prefer crop when it is at least as good and looks more like real dialogue
        /// (catches myself vs money when scores are close).
        /// </summary>
        public static bool PreferCropWording(string crop, string fullUnit)
        {
            if (SpeechCleaner.IsUnusableOcrText(crop) || string.IsNullOrWhiteSpace(fullUnit))
                return false;
            if (string.Equals(
                    crop.Trim(), fullUnit.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            int qc = SpeechCleaner.OcrTextQualityScore(crop);
            int qf = SpeechCleaner.OcrTextQualityScore(fullUnit);
            // Crop wins ties and near-ties
            if (qc >= qf - 1 && SpeechCleaner.CountAlnum(crop) >= SpeechCleaner.CountAlnum(fullUnit) - 2)
                return true;
            if (qc >= qf && ComicRegionGeometry.CountWords(crop) >= ComicRegionGeometry.CountWords(fullUnit))
                return true;
            // Crop has vowels / letter density at least as good on a short line
            if (ComicRegionGeometry.CountWords(crop) <= 8 && qc + 2 >= qf)
                return true;
            return false;
        }

        /// <summary>
        /// Count content tokens (length ≥ <paramref name="minLen"/>) present in
        /// <paramref name="a"/> but not in <paramref name="b"/>. Detects full-frame
        /// tails crops dropped ("beginning", "next", "was").
        /// </summary>
        public static int CountContentTokensOnlyInA(
            string a, string b, int minLen = 4)
        {
            if (string.IsNullOrWhiteSpace(a))
                return 0;
            minLen = Math.Max(2, minLen);
            var tb = SpeechCleaner.ToTokenSet(b ?? "");
            int n = 0;
            foreach (string t in SpeechCleaner.ToTokenSet(a))
            {
                if (t.Length >= minLen && !tb.Contains(t))
                    n++;
            }
            return n;
        }

        /// <summary>
        /// Best matching speak unit index for <paramref name="text"/>, or -1.
        /// Overlap = |shared tokens| / max(|a|,|b|) (Jaccard-like coverage).
        /// </summary>
        public static int FindBestMatchingUnit(
            string text,
            List<string> units,
            double minOverlap)
        {
            int best = -1;
            double bestOv = minOverlap - 1e-6;
            for (int i = 0; i < units.Count; i++)
            {
                double ov = SpeechCleaner.TokenOverlapRatio(text, units[i]);
                if (ov >= minOverlap && ov > bestOv)
                {
                    bestOv = ov;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// True when crop text looks like truncated VL mush that mostly restates
        /// full-frame tokens (many short stems that are prefixes of full-frame words).
        /// </summary>
        public static bool LooksLikeTruncatedOcrGarbage(string crop, string fullText)
        {
            if (string.IsNullOrWhiteSpace(crop))
                return true;

            var cropToks = SpeechCleaner.TokenizeWords(crop)
                .Select(SpeechCleaner.NormalizeToken)
                .Where(t => t.Length >= 3)
                .ToList();
            if (cropToks.Count < 3)
                return false;

            var fullToks = SpeechCleaner.ToTokenSet(fullText ?? "");

            int stubby = 0;
            int prefixOfFull = 0;
            foreach (string t in cropToks)
            {
                bool hasVowel = t.IndexOfAny(new[] { 'a', 'e', 'i', 'o', 'u', 'y' }) >= 0;
                if (!hasVowel)
                {
                    stubby++;
                    continue;
                }

                // Token is a strict prefix of a longer full-frame word → truncation.
                // Skip stopwords — they are not OCR cuts.
                if (!IsWeakNovelToken(t) &&
                    IsStrictPrefixOfAny(t, fullToks) &&
                    t.Length <= 9)
                {
                    prefixOfFull++;
                }

                // Stubby mid-words: only when the token looks cut mid-stem
                // (prefix of a full word).
                if (t.Length is >= 4 and <= 8 &&
                    !t.EndsWith("ing", StringComparison.Ordinal) &&
                    !t.EndsWith("ed", StringComparison.Ordinal) &&
                    !t.EndsWith("ly", StringComparison.Ordinal) &&
                    !t.EndsWith("er", StringComparison.Ordinal) &&
                    !t.EndsWith("es", StringComparison.Ordinal) &&
                    !t.EndsWith("ion", StringComparison.Ordinal) &&
                    !fullToks.Contains(t) &&
                    IsStrictPrefixOfAny(t, fullToks))
                {
                    stubby++;
                }
            }

            if (prefixOfFull >= 2)
                return true;
            if (stubby >= Math.Max(3, cropToks.Count / 2))
                return true;
            return false;
        }

        /// <summary>Stopwords / tiny tokens that must not drive trunc/cover gates.</summary>
        public static bool IsWeakNovelToken(string normToken)
        {
            if (string.IsNullOrEmpty(normToken) || normToken.Length <= 2)
                return true;
            // Common function words + short openers that are often prefixes of longer words
            return normToken is
                "the" or "and" or "for" or "that" or "with" or "this" or "from" or
                "have" or "been" or "were" or "was" or "are" or "you" or "your" or
                "they" or "them" or "then" or "than" or "when" or "what" or "who" or
                "into" or "onto" or "not" or "but" or "all" or "any" or "can" or
                "her" or "his" or "our" or "out" or "how" or "now" or "its" or
                "let" or "get" or "got" or "did" or "has" or "had" or "she" or
                "him" or "may" or "will" or "just" or "only" or "also" or "than";
        }

        public static bool IsStrictPrefixOfAny(string token, HashSet<string> fullToks)
        {
            foreach (string f in fullToks)
            {
                if (f.Length > token.Length + 1 &&
                    f.StartsWith(token, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Content tokens (len≥4, has vowel, not stopword) in <paramref name="text"/>
        /// that are absent from <paramref name="knownTok"/>.
        /// Prefixes of known tokens do not count as novel.
        /// </summary>
        public static void CountNovelContentTokens(
            string text,
            HashSet<string> knownTok,
            out int novelContent,
            out int contentTotal)
        {
            novelContent = 0;
            contentTotal = 0;
            if (string.IsNullOrWhiteSpace(text) || knownTok == null)
                return;

            foreach (string raw in SpeechCleaner.TokenizeWords(text))
            {
                string t = SpeechCleaner.NormalizeToken(raw);
                if (t.Length < 4)
                    continue;
                if (IsWeakNovelToken(t))
                    continue;
                if (t.IndexOfAny(new[] { 'a', 'e', 'i', 'o', 'u', 'y' }) < 0)
                    continue;
                contentTotal++;
                if (knownTok.Contains(t))
                    continue;
                // Truncated stem of a spoken word is not a new balloon
                if (IsStrictPrefixOfAny(t, knownTok))
                    continue;
                novelContent++;
            }
        }

        /// <summary>
        /// True when <paramref name="sn"/> looks like a broken restatement of an
        /// already-kept full unit (mid-word tails, no sentence end, high join cover).
        /// </summary>
        public static bool LooksLikeWeakCaptionScrap(
            string sn,
            List<string> result,
            double alreadyJoin)
        {
            if (string.IsNullOrWhiteSpace(sn) || result == null || result.Count == 0)
                return false;

            string t = sn.Trim();
            int sw = ComicRegionGeometry.CountWords(t);
            if (sw < 4)
                return false;

            // Ends mid-token garbage ("strikes ho", "…phan", "warnthere")
            bool openEnd = !Regex.IsMatch(t, @"[.!?]['\u2019\u201D)]*\s*$");
            bool midWordTail = Regex.IsMatch(t,
                @"\b(?:b|ho|phan|warnthere|srorm|colp|gops)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                Regex.IsMatch(t, @"\b\p{L}{1,2}\s*$"); // trailing 1–2 letter stub only

            if (alreadyJoin >= 0.35 && openEnd && midWordTail)
                return true;

            // Much shorter than a full unit that already covers most of this snip
            foreach (string u in result)
            {
                if (ComicRegionGeometry.CountWords(u) < sw + 4)
                    continue;
                double cover = SpeechCleaner.TokenCoverageOfAByB(t, u);
                if (cover >= 0.55 && SpeechCleaner.OcrTextQualityScore(u) >= SpeechCleaner.OcrTextQualityScore(t))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Quality gate for crop novel-inserts.
        /// Rejects VL garbage ("yeah i hea codger ca") but keeps short real dialogue
        /// ("it's still", "rotten!", "maybe so, mate.").
        /// </summary>
        public static bool IsUsableNovelCropInsert(string text)
        {
            if (SpeechCleaner.IsUnusableOcrText(text) || IsJunkWinOcrText(text))
                return false;

            int words = ComicRegionGeometry.CountWords(text);
            int alnum = SpeechCleaner.CountAlnum(text);
            // Floor is 2 so single punchy balloons ("no", "ok") can still insert;
            // longer novel inserts still need the quality checks below.
            if (alnum < 2)
                return false;

            var toks = SpeechCleaner.TokenizeWords(text);
            if (toks.Count == 0)
                return false;

            // Content word: =4 letters with a vowel (comic call-outs / short balloons)
            bool hasContentWord = toks.Any(t =>
                t.Length >= 4 &&
                t.IndexOfAny(new[] { 'a', 'e', 'i', 'o', 'u', 'y' }) >= 0);

            // Short real dialogue - do not require 3+ tokens
            // (apostrophes split "it's" → it/s; still valid English)
            if (words <= 3 || toks.Count <= 3)
            {
                if (hasContentWord && alnum >= 5)
                    return true;
                // Two solid short words ("oh no", "my god") with enough letters
                if (words >= 2 && alnum >= 6 &&
                    toks.Count(t => t.Length >= 2) >= 2)
                    return true;
                // Single punchy word balloon ("ROTTEN!", "No!", "OK!")
                if (words == 1 && alnum >= 2 &&
                    toks.Count == 1 &&
                    LooksLikeRealDialogueToken(toks[0]))
                    return true;
                return false;
            }

            // Longer text: reject truncated VL mash (majority tiny stubs, no content words)
            int shorty = toks.Count(t => t.Length <= 2);
            if (!hasContentWord && shorty >= (toks.Count + 1) / 2)
                return false;

            int withVowel = toks.Count(t =>
                t.IndexOfAny(new[] { 'a', 'e', 'i', 'o', 'u', 'y' }) >= 0);
            if (withVowel < Math.Max(1, toks.Count / 4))
                return false;

            return true;
        }

        /// <summary>
        /// Pull speakable residual spans from full-frame text that crops missed
        /// (e.g. "and it was.", "next: first friends!"). Returns text + char index
        /// in <paramref name="full"/> for story ordering among crops.
        /// </summary>
        public static List<(string Text, int CharPos)> ExtractResidualFullSpans(
            string full,
            HashSet<string> cropTok)
        {
            var result = new List<(string Text, int CharPos)>();
            if (string.IsNullOrWhiteSpace(full) || cropTok == null || cropTok.Count == 0)
                return result;

            // Sentence / clause units first
            int searchFrom = 0;
            foreach (string unit in SplitIntoSpeakUnits(full))
            {
                if (SpeechCleaner.IsUnusableOcrText(unit))
                    continue;

                CountNovelTokens(unit, cropTok, out int novel, out int total);
                if (total == 0 || novel == 0)
                    continue;

                double frac = novel / (double)total;
                int alnum = SpeechCleaner.CountAlnum(unit);
                int words = ComicRegionGeometry.CountWords(unit);

                bool shortResidual =
                    words <= 6 &&
                    alnum >= 4 &&
                    (
                        (novel >= 2 && frac >= 0.35) ||
                        (novel >= 1 && frac >= 0.25 && alnum <= 40)
                    );
                bool longResidual =
                    words > 6 &&
                    novel >= 2 &&
                    frac >= 0.45 &&
                    alnum >= 8;

                if (!shortResidual && !longResidual)
                    continue;

                if (SpeechCleaner.TokenCoverageOfAByB(unit, string.Join(" ", cropTok)) >= 0.88 &&
                    frac < 0.50)
                    continue;

                string trimmed = unit.Trim();
                int pos = IndexOfSpeakableInText(full, trimmed, searchFrom);
                if (pos < 0)
                    pos = IndexOfSpeakableInText(full, trimmed);
                if (pos >= 0)
                    searchFrom = pos + Math.Max(1, trimmed.Length / 2);
                result.Add((trimmed, pos));
            }

            if (result.Count > 0)
                return DedupResidualList(result);

            // Monoblock: contiguous novel runs (minRun=1 for NEXT-style banners)
            foreach (var (run, pos) in ExtractAllContiguousNovelRuns(full, cropTok, minRun: 1))
            {
                if (SpeechCleaner.IsUnusableOcrText(run))
                    continue;
                if (SpeechCleaner.CountAlnum(run) < 4)
                    continue;
                result.Add((run.Trim(), pos));
            }

            return DedupResidualList(result);
        }

        public static List<(string Text, int CharPos)> DedupResidualList(
            List<(string Text, int CharPos)> spans)
        {
            var kept = new List<(string Text, int CharPos)>();
            foreach (var (s, pos) in spans)
            {
                bool dup = false;
                for (int i = 0; i < kept.Count; i++)
                {
                    if (SpeechCleaner.TokenOverlapRatio(s, kept[i].Text) >= 0.80)
                    {
                        if (SpeechCleaner.CountAlnum(s) > SpeechCleaner.CountAlnum(kept[i].Text) ||
                            SpeechCleaner.IsClearlyBetterOcr(s, kept[i].Text))
                            kept[i] = (s, pos >= 0 ? pos : kept[i].CharPos);
                        dup = true;
                        break;
                    }
                }
                if (!dup)
                    kept.Add((s, pos));
            }
            return kept;
        }

        /// <summary>
        /// Place a residual span among crop snip geometry orders using its character
        /// position in the full-frame unit (snips that start earlier in full → before).
        /// </summary>
        public static double OrderResidualAmongCrops(
            string fullUnit,
            int residualPos,
            string residual,
            List<(string Text, double Order)> cropSnips,
            double cropsEndFallback)
        {
            if (cropSnips.Count == 0)
                return cropsEndFallback;

            if (residualPos < 0)
                residualPos = IndexOfSpeakableInText(fullUnit, residual);

            double bestBefore = double.NegativeInfinity;
            double bestAfter = double.PositiveInfinity;

            foreach (var (text, order) in cropSnips)
            {
                int snipPos = IndexOfSpeakableInText(fullUnit, text);
                if (snipPos < 0)
                {
                    // Fallback: first content token of the snip
                    var toks = SpeechCleaner.TokenizeWords(text);
                    string? key = toks.FirstOrDefault(t => t.Length >= 4)
                                  ?? toks.FirstOrDefault(t => t.Length >= 3);
                    if (key != null)
                        snipPos = IndexOfSpeakableInText(fullUnit, key);
                }

                if (snipPos < 0)
                    continue;

                // Snip starts before residual body → residual comes after this snip
                if (residualPos < 0 || snipPos + 3 < residualPos)
                    bestBefore = Math.Max(bestBefore, order);
                else if (snipPos > residualPos + 2)
                    bestAfter = Math.Min(bestAfter, order);
            }

            if (!double.IsNegativeInfinity(bestBefore) &&
                !double.IsPositiveInfinity(bestAfter) &&
                bestAfter > bestBefore)
                return (bestBefore + bestAfter) / 2.0;

            if (!double.IsNegativeInfinity(bestBefore))
                return bestBefore + 0.35;

            if (!double.IsPositiveInfinity(bestAfter))
                return bestAfter - 0.35;

            return cropsEndFallback;
        }

        /// <summary>
        /// Approximate start index of <paramref name="span"/> inside <paramref name="hay"/>
        /// (case-insensitive; falls back to first long content word).
        /// </summary>
        public static int IndexOfSpeakableInText(
            string hay, string span, int startIndex = 0)
        {
            if (string.IsNullOrWhiteSpace(hay) || string.IsNullOrWhiteSpace(span))
                return -1;

            startIndex = Math.Clamp(startIndex, 0, hay.Length);
            int i = hay.IndexOf(span.Trim(), startIndex, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
                return i;

            // First 3–4 content words as a soft search
            var words = SpeechCleaner.TokenizeWords(span)
                .Where(w => w.Length >= 3)
                .Take(4)
                .ToList();
            if (words.Count == 0)
                return -1;

            string needle = words[0];
            int from = startIndex;
            while (from < hay.Length)
            {
                int at = hay.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    break;

                // Verify following words appear nearby in order
                int cursor = at + needle.Length;
                bool ok = true;
                for (int w = 1; w < words.Count; w++)
                {
                    int n = hay.IndexOf(
                        words[w], cursor, StringComparison.OrdinalIgnoreCase);
                    if (n < 0 || n > cursor + 48)
                    {
                        ok = false;
                        break;
                    }
                    cursor = n + words[w].Length;
                }

                if (ok)
                    return at;
                from = at + 1;
            }

            return hay.IndexOf(needle, startIndex, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// All contiguous novel-word runs in <paramref name="text"/> (tokens not in
        /// <paramref name="knownTok"/>). <paramref name="minRun"/>=1 allows a single
        /// banner word like NEXT when it is long enough after alnum filter.
        /// </summary>
        public static List<(string Text, int CharPos)> ExtractAllContiguousNovelRuns(
            string text,
            HashSet<string> knownTok,
            int minRun = 2)
        {
            var result = new List<(string Text, int CharPos)>();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            var tokens = new List<(int Start, int End, bool Novel)>();
            int i = 0;
            string s = text;
            while (i < s.Length)
            {
                while (i < s.Length && !char.IsLetterOrDigit(s[i]))
                    i++;
                if (i >= s.Length)
                    break;
                int start = i;
                while (i < s.Length && char.IsLetterOrDigit(s[i]))
                    i++;
                string raw = s[start..i];
                string norm = SpeechCleaner.NormalizeToken(raw);
                bool novel = norm.Length > 0 && !knownTok.Contains(norm);
                tokens.Add((start, i, novel));
            }

            int runStart = -1;
            for (int t = 0; t <= tokens.Count; t++)
            {
                bool novel = t < tokens.Count && tokens[t].Novel;
                if (novel)
                {
                    if (runStart < 0)
                        runStart = t;
                }
                else if (runStart >= 0)
                {
                    int runEnd = t - 1;
                    int runLen = runEnd - runStart + 1;
                    if (runLen >= minRun)
                    {
                        int charStart = tokens[runStart].Start;
                        int charEnd = tokens[runEnd].End;
                        while (charEnd < s.Length &&
                               (s[charEnd] is '.' or '!' or '?' or ',' or ';' or ':' or '"' or '\''))
                            charEnd++;
                        string span = s[charStart..charEnd].Trim();
                        if (span.Length > 0)
                            result.Add((span, charStart));
                    }
                    runStart = -1;
                }
            }

            return result;
        }

        /// <summary>
        /// Pull speakable novel content from a crop that partially overlaps full-frame.
        /// Prefers sentence/clause units; falls back to the longest contiguous novel
        /// word run (for missing tails like "rank has its privileges").
        /// </summary>
        public static string? ExtractNovelCropSpans(
            string crop,
            HashSet<string> fullTok,
            out int novelWordCount)
        {
            novelWordCount = 0;
            if (string.IsNullOrWhiteSpace(crop))
                return null;

            var units = SplitIntoSpeakUnits(crop);
            var kept = new List<string>();
            int novelSum = 0;
            foreach (string unit in units)
            {
                CountNovelTokens(unit, fullTok, out int novel, out int total);
                if (total == 0)
                    continue;
                double frac = novel / (double)total;
                if (!IsNovelEnoughUnit(novel, total, frac))
                    continue;
                kept.Add(unit.Trim());
                novelSum += novel;
            }

            if (kept.Count > 0)
            {
                novelWordCount = novelSum;
                return string.Join(" ", kept);
            }

            // No sentence boundary cleanly isolated the novel tail - use runs
            return ExtractContiguousNovelRun(crop, fullTok, out novelWordCount);
        }

        /// <summary>
        /// Split crop text into speakable units (sentences / short paragraphs).
        /// </summary>
        public static List<string> SplitIntoSpeakUnits(string text)
        {
            var units = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return units;

            // Paragraph breaks first
            string[] paras = Regex.Split(text.Trim(), @"\n\s*\n+");
            foreach (string para in paras)
            {
                string p = para.Trim();
                if (p.Length == 0)
                    continue;

                // Sentence-ish: end punctuation + space/newline/end, keep punct on unit
                var matches = Regex.Matches(
                    p,
                    @"[^.!?]+(?:[.!?]+(?:\s+|$)|$)",
                    RegexOptions.Singleline);
                if (matches.Count == 0)
                {
                    units.Add(p);
                    continue;
                }

                foreach (Match m in matches)
                {
                    string u = m.Value.Trim();
                    if (u.Length > 0)
                        units.Add(u);
                }
            }

            // Single blob if split produced nothing useful
            if (units.Count == 0)
                units.Add(text.Trim());
            return units;
        }

        public static void CountNovelTokens(
            string text,
            HashSet<string> fullTok,
            out int novel,
            out int total)
        {
            novel = 0;
            total = 0;
            foreach (string w in SpeechCleaner.TokenizeWords(text))
            {
                total++;
                if (!fullTok.Contains(SpeechCleaner.NormalizeToken(w)))
                    novel++;
            }
        }

        /// <summary>
        /// Whether a speak unit has enough novel words to speak after full-frame.
        /// </summary>
        public static bool IsNovelEnoughUnit(int novel, int total, double frac)
        {
            if (novel < 2 || total <= 0)
                return false;
            // Short reply / rank-line balloons
            if (total <= 6)
                return frac >= 0.45;
            // Medium sentence
            if (total <= 12)
                return novel >= 3 && frac >= 0.40;
            // Long unit: need majority novel (avoid restating + 3 OCR glitches)
            return novel >= 4 && frac >= 0.50;
        }

        /// <summary>
        /// Longest contiguous novel-word run mapped back onto the original crop text.
        /// Recovers missing tails glued to an already-spoken balloon in one crop.
        /// </summary>
        public static string? ExtractContiguousNovelRun(
            string crop,
            HashSet<string> fullTok,
            out int novelWordCount)
        {
            novelWordCount = 0;
            if (string.IsNullOrWhiteSpace(crop))
                return null;

            // (start, end exclusive, isNovel) per token in original string
            var tokens = new List<(int Start, int End, bool Novel)>();
            int i = 0;
            string s = crop;
            while (i < s.Length)
            {
                while (i < s.Length && !char.IsLetterOrDigit(s[i]))
                    i++;
                if (i >= s.Length)
                    break;
                int start = i;
                while (i < s.Length && char.IsLetterOrDigit(s[i]))
                    i++;
                string raw = s[start..i];
                string norm = SpeechCleaner.NormalizeToken(raw);
                bool novel = norm.Length > 0 && !fullTok.Contains(norm);
                tokens.Add((start, i, novel));
            }

            if (tokens.Count == 0)
                return null;

            int bestStart = -1, bestEnd = -1, bestLen = 0;
            int runStart = -1, runLen = 0;
            for (int t = 0; t < tokens.Count; t++)
            {
                if (tokens[t].Novel)
                {
                    if (runStart < 0)
                        runStart = t;
                    runLen++;
                    if (runLen > bestLen)
                    {
                        bestLen = runLen;
                        bestStart = runStart;
                        bestEnd = t;
                    }
                }
                else
                {
                    runStart = -1;
                    runLen = 0;
                }
            }

            // Need a real multi-word novel span (single junk token is not enough)
            if (bestLen < 2 || bestStart < 0)
                return null;

            int charStart = tokens[bestStart].Start;
            int charEnd = tokens[bestEnd].End;
            // Include trailing sentence punctuation after the run
            while (charEnd < s.Length &&
                   (s[charEnd] is '.' or '!' or '?' or ',' or ';' or ':' or '"' or '\''))
                charEnd++;

            string span = s[charStart..charEnd].Trim();
            if (span.Length == 0 || SpeechCleaner.IsUnusableOcrText(span))
                return null;

            novelWordCount = bestLen;
            return span;
        }
        /// <summary>
        /// True when WinOCR text is empty or below the minimum alphanumeric floor.
        /// </summary>
        public static bool IsJunkWinOcrText(string? text, int minAlnumChars = 2)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;
            if (SpeechCleaner.CountAlnum(text) < minAlnumChars)
                return true;
            return false;
        }

        /// <summary>
        /// True for a short alnum token that looks like real English dialogue
        /// (letter-only, has a vowel). Includes punchy 2-3 letter balloons
        /// ("NO", "OK", "GO", "YES") and longer call-outs.
        /// </summary>
        public static bool LooksLikeRealDialogueToken(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            string n = SpeechCleaner.NormalizeToken(text);
            // 2–18 letters: "no"/"ok" through multi-syllable SFX names
            if (n.Length < 2 || n.Length > 18)
                return false;
            foreach (char c in n)
            {
                if (!char.IsLetter(c))
                    return false;
            }
            foreach (char c in n)
            {
                char l = char.ToLowerInvariant(c);
                if (l is 'a' or 'e' or 'i' or 'o' or 'u' or 'y')
                    return true;
            }
            return false;
        }
    }
}