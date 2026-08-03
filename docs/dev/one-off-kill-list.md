# One-off kill list (PR plan)

**Goal:** Remove single-page / single-log workarounds so real bugs can reappear and be fixed with **category-level** rules (or left as known gaps until then).

**Not the goal:** Rewrite the comic pipeline in one PR. Each PR should be small, testable, and reversible.

**Companion scan:** Inventory from agent scan (2026-08). Production has **no** hard-coded page paths/IDs — only heuristics and comments born from named panels.

---

## Ground rules (every PR)

1. **One kill theme per PR** (table row / PR id below). Do not mix geometry order with speech-noise in the same PR.
2. **Same PR updates** ModeSmoke / unit tests that *assert* the deleted behavior. Do **not** leave green tests that re-require a one-off.
3. **Do not** reintroduce the fix under a new name in the same PR (“while we’re here…”).
4. **Comments:** strip panel nicknames (`Emplate`, `Storm`, `Hattie`, `Adrienne`, `jazzed-hero`, `sailors`, candy jar) when the code is deleted or generalized. Page names belong in git history / bug trackers, not production comments that justify permanent branches.
5. **Verify:** `dotnet test tests/SpeakRect.Tests` + relevant smoke (`tests/ModeSmoke` at minimum when touching OCR/speech). Manual: 3–5 *unrelated* comic pages, not only the original.
6. **After remove:** if a page breaks, open a ticket for a **general** fix; do not immediately restore the page branch.
7. **Keep:** user refine (`ComicRegionOverrideSession`), settings knobs, and pure regression *fixtures* only when they still test remaining general code.

### Policy for new code (after Phase 0)

- No new `if` / threshold / noise rule justified by a single panel name in comments.
- New heuristics need a **category** description (e.g. “mega detect island”, “VL invents C types”, “JSON field wrappers”) and ≥2 distinct failure shapes or a property-based argument.
- Prefer settings or measurable metrics over magic constants tuned to one dump.

---

## Dependency order (why this sequence)

```
Phase 0  Policy + inventory lock
   │
   ├─► P1  Isolated string/token gates (uchar)          ← no geometry / fusion deps
   ├─► P2  Art-ghost token class (coocoo-shaped)        ← dead-island / dyn-fog callers
   ├─► P3  Dead-island “cream logo” plate gate          ← after P2 (same FilterDead path)
   ├─► P4  Speak-dedupe short/mega echoes (Emma/sailors)← SpeechCleaner only
   ├─► P5  JSON unwrap page fixtures (Hattie/Adrienne)  ← keep general unwrap; kill page lock-in
   ├─► P6  Storm non-Latin + trunc-escape hatch         ← fusion novel path
   ├─► P7  MinSpeakUnitWords / afternoon superset       ← fusion wording preference
   ├─► P8  Emplate mega full-frame branch + thresholds  ← sequential path; geometry helper
   └─► P9  Reading-order stack/strip (jazzed-hero/sailors) ← geometry topo edges
```

Later phases depend on earlier speech filters only loosely; **geometry (P8–P9) is last** because it changes speak *order* for many pages and is hardest to re-validate. **Token/string kills first** maximize learning with minimal cascade.

---

## PR kill list

### Phase 0 — Policy only (no behavior change)

| | |
|--|--|
| **PR id** | `P0-policy` |
| **Scope** | This file + short note in `docs/architecture/speak-path-checklist.md` (link + “no page-named special cases”). |
| **Delete** | Nothing in product code. |
| **Tests** | None. |
| **Done when** | Reviewers agree purge order; no product diff. |

---

### P1 — `uchar` / unsigned-char one-token gate

| | |
|--|--|
| **PR id** | `P1-uchar` |
| **Why first** | Pure noise / usability; no detect geometry; invented by one mega-crop archive. |
| **Delete / revert** | |
| | • Built-in rule `noise-c-type-uchar` in `SpeechTextRule.cs` (~303–308) |
| | • Lone-token unusable check in `SpeechCleaner.IsUnusableOcrText` (~352–356) matching `uchar` / `unsigned char` |
| | • ModeSmoke block “Filter uchar…” and catalog assert for `noise-c-type-uchar` |
| **Keep** | Generic empty/prompt-echo/refusal gates in `IsUnusableOcrText`. |
| **Expected fallout** | If VL invents `uchar` again, TTS may speak it — **desired** so a general “non-dialogue programming token” class can be designed later. |
| **Do not** | Add a longer list of C types in the same PR. |
| **Smoke** | ModeSmoke speech/noise sections; unit speech tests if any assert uchar. |

**Replacement (later, separate PR if needed):** class of VL programming junk (identifier-like tokens, type keywords) with tests on synthetic garbage — not comic dialogue.

---

### P2 — Repeated-syllable art ghost (`coocoo` / candy jar)

| | |
|--|--|
| **PR id** | `P2-coocoo` |
| **Depends on** | P1 (optional; independent if preferred). |
| **Delete / soften** | |
| | • Call sites that **nuke solely** via `LooksLikeRepeatedSyllableGibberish` in dyn-fog verify (`OcrProcessor` ~5802–5810) and dead-island path comments tying to jar |
| | • **Decision fork (pick one in PR description):** |
| | **A (aggressive kill):** Remove `LooksLikeRepeatedSyllableGibberish` + `LooksLikeRealDialogueToken` rejection of it; drop unit tests `Dyn_fog_rejects_candy_jar_coocoo_gibberish`, `Dead_island_drops_coocoo_jar_gibberish`. |
| | **B (narrow kill):** Keep helper but stop using it as a hard nuke; rely only on balloon-fill / min-alnum. |
| **Recommend** | **A** if goal is “no single-case filters”; **B** if coocoo-class already feels general enough — then **rename** to category language and drop jar comments, still count as *keep not kill*. |
| **Expected fallout** | Art OCR may reintroduce SFX-like ghosts on shiny props. |
| **Replacement (later)** | Texture/balloon-fill confidence + optional user “min island alnum” only. |

---

### P3 — Dead-island “cream” / non-balloon single token

| | |
|--|--|
| **PR id** | `P3-cream-logo` |
| **Depends on** | P2 if you touch the same `FilterDeadDetectRegions` loops. |
| **Target** | `OcrProcessor.FilterDeadDetectRegions` branch ~9207–9219: `words <= 1 && !LooksLikeSpeechBalloonFill` with *cream / Feth / 2026-07-29* comments. |
| **Delete / soften** | |
| | • **A:** Drop the non-balloon-token branch entirely (keep weak-ocr / empty-small / single-token-scrap if still category-level). |
| | • **B:** Keep balloon-fill as a **general** rule but remove logo-named comments and ModeSmoke/GeometryPipeline “cream” fixture as *required* behavior. |
| **Tests** | `GeometryPipelineTests.Dead_island_drops_art_logo_token` (“cream”); ModeSmoke dead-island / cream logo anchors in checklist. |
| **Expected fallout** | Logos / title lettering may become speak islands again. |
| **Replacement (later)** | Balloon-fill score + area + alnum as one metric; no token allowlist of brand words. |

---

### P4 — Speak-dedupe: Emma “really?” + sailors mega-echo thresholds

| | |
|--|--|
| **PR id** | `P4-speak-dedupe` |
| **Target** | `SpeechCleaner.DedupeSpeakUnitsForTts` (~156–308): short-balloon keep, mega-echo coverage tiers, comments *sailors-singapore* / *really?* / *wondrous beginning*. |
| **Delete / soften** | |
| | • Short-unit special case (`words <= 2` similar-length prior) if it exists only for Emma-style replies. |
| | • Over-tuned multi-tier `isEcho` thresholds if they were dialed to one mega caption dump — **or** simplify to one coverage rule. |
| **Caution** | Pure global dedupe is load-bearing for crop-echo. Prefer **simplifying** thresholds over deleting the whole method. |
| **Tests** | ModeSmoke Emma panel block (~839–873); sailors mega-echo check. |
| **Expected fallout** | Short replies may drop when stem appears earlier; or crop echoes may double-speak. |
| **Replacement (later)** | Geometry-aware dedupe (same island / same region index) instead of pure token bag. |

---

### P5 — JSON unwrap: strip page lock-in, keep general unwrap

| | |
|--|--|
| **PR id** | `P5-json-unwrap` |
| **Target** | `SpeechCleaner.UnwrapModelJsonPayload` / `UnwrapLooseJsonTextAssignments`. |
| **Kill (page lock-in)** | Comments and ModeSmoke raw strings that **require** Adrienne/Hattie dialogue as the only golden path. |
| **Keep (general)** | Unwrap of `{"text":…}`, loose `"text": "…"`, prompt contamination strip — these are model-format issues, not comic layouts. |
| **Action** | |
| | 1. Rewrite comments to “VL freestyle JSON field” only. |
| | 2. Replace ModeSmoke fixtures with **synthetic** names/dialogue so tests do not encode a personal archive page. |
| | 3. Do **not** delete unwrap unless you accept speaking `text` / `n` leftovers again. |
| **Note** | This PR is a **de-personalize + document as category** PR, not a pure delete — unless you explicitly choose full unwrap removal (not recommended). |

---

### P6 — Storm: non-Latin noise + trunc-garbage escape hatch

| | |
|--|--|
| **PR id** | `P6-storm-fusion` |
| **Targets** | |
| | • `SpeechCleaner.IsMostlyNonLatinLetterNoise` usage in `IsUnusableOcrText` (Storm dual panel comment). |
| | • `ComicBestOfFusion.LooksLikeTruncatedOcrGarbage` “novel complete ≥ 2 → not garbage” escape (~1536–1555) and stubby/prefix logic fed by Storm / phantom examples. |
| **Delete / soften** | |
| | • **Non-Latin:** keep only if treated as general “script spam” with a unit test on synthetic codepoints (no panel name). Else remove gate. |
| | • **Escape hatch:** remove novel-complete early `return false` and re-run fusion tests; or replace with a single measurable rule. |
| **Tests** | Any ModeSmoke/unit covering trunc/novel inserts; add synthetic non-Latin only if gate stays. |
| **Expected fallout** | Right-column novel balloons may be treated as trunc mush again; or Linear-B spam may speak. |
| **Replacement (later)** | Confidence from crop vs full token IoU; charset allow-list as settings. |

---

### P7 — “Afternoon” / one-word openers + crop tight superset

| | |
|--|--|
| **PR id** | `P7-afternoon` |
| **Targets** | |
| | • `MinSpeakUnitWords = 1` comments about afternoon/`No!` in `OcrProcessor` / `ComicBestOfFusion`. |
| | • `cropTightSuperset` branch in full-order merge (`ComicBestOfFusion` ~551–573). |
| **Delete / soften** | |
| | • Revert superset preference (always keep full when scores tie / incomplete). |
| | • Optionally restore min words ≥ 2 **only if** short-balloon path is still protected by `IsUnusableOcrText` + expand units (do not re-break `No!`). |
| **Caution** | Short balloons (`No!`, `OK!`) are **product** behavior, not Emplate — do not sacrifice them to purge afternoon. Split: keep short-dialogue survival; kill only “good afternoon” superset preference if it is the one-off. |
| **Tests** | ModeSmoke short balloons; fusion-related if any. |

---

### P8 — Emplate mega full-frame path

| | |
|--|--|
| **PR id** | `P8-emplates-mega` |
| **Depends on** | Prefer after P1–P4 so speech noise does not mask geometry fallout. |
| **Targets** | |
| | • Sequential path branch: single region + `RegionIsNearFullFrame` → `RunFullFrameWithWideRescueAsync` (`OcrProcessor` ~3177–3200+). |
| | • Thresholds in `ComicRegionGeometry.RegionIsNearFullFrame` (0.42 / 0.82 / 0.50) if they only exist to match Emplate archive box. |
| | • ModeSmoke Emplate-like mega region + EMPLATE under-read text (~478–501). |
| **Delete / soften** | |
| | 1. Remove sequential mega → full-frame special branch; always use per-region crop path for count==1. |
| | 2. Either delete `RegionIsNearFullFrame` or retune only if another **documented category** needs it (with new multi-page evidence). |
| | 3. Audit other call sites of `RegionIsNearFullFrame` (~3274, 3339) — same PR or immediate follow-up; do not leave half the mega logic. |
| **Expected fallout** | Mega islands may mid-panel again (the original Emplate bug). |
| **Replacement (later)** | Compare crop word-count / coverage to full-frame or to WinOCR island text (`KoboldUnderReadsWinOcr` already exists — prefer metric-driven rescue over area fractions). |

---

### P9 — Reading-order: jazzed-hero continuity + sailors strip peers

| | |
|--|--|
| **PR id** | `P9-reading-order` |
| **Depends on** | Last: order changes are global. |
| **Targets** | `ComicRegionGeometry.ApplyLightStackPreference` stack-continuity loop (~465–486); nested/strip rules in `BoxesNestedOrMostlyContained` / `IsSameRowLeftRight` with sailors/caption-callout comments. |
| **Delete / soften** | |
| | • Remove **stack continuity** edges (finish B before C) only — leave simple band L→R + vertical stack. |
| | • Optionally simplify nested-strip “Y-containment” if it only fixed sailors/caption pages. |
| **Tests** | ModeSmoke caption-before-callout, 2×2 grid, vertical stack, side-by-side (keep grid/stack; drop or rewrite continuity-specific cases if any). |
| **Expected fallout** | Some pages speak right singleton before finishing left column; caption vs callout order may flip. |
| **Replacement (later)** | Explicit reading-order model (rows bands + column stacks) with multi-page golden geometries, not named panels. |

---

## Explicitly out of scope (do not “kill” as one-offs)

| Item | Why keep |
|------|----------|
| `ComicRegionOverrideSession` | User session refine, not a baked page fix |
| Settings comic knobs (fog, inflate, sequential, min alnum) | User-controlled |
| Short balloon survival `No!`/`OK!` | Product requirement; multi-page |
| Prompt-echo / markdown / assistant chrome strip | Model hygiene, not layout |
| Wide-strip L/R rescue (P8 adjacent) | Category “wide dual balloon”; optional **P10** if you later decide it is archive-tuned only |
| Wide-ribbon POI min height (`IslandStripMinHeight`) | Optional **P11** — comment `191×88` is a smell; kill only after POI re-test matrix |
| Retired `abbrev-max` / `abbrev-min` absence | Correct product behavior for names; not a page hack to re-add |

---

## Optional later PRs (not core kill path)

| PR id | Theme |
|-------|--------|
| `P10-wide-strip` | Revisit `WideStripMinAspect` / `WideStripMaxWordsBeforeSplit` if only dual-bubble archive motivated |
| `P11-poi-ribbon` | Revisit `IsWideThinIslandStrip` / `191×88` exclusion |
| `P12-winocr-anchor` | Document intentional **absence** of short-token winocr-anchor gate (comment ~3396); do not silently re-add |
| `P13-comments-only` | Repo-wide strip of remaining panel nicknames after code kills land |

---

## Suggested Graphite / branch stack

```
main
 └─ P0-policy
     └─ P1-uchar
         └─ P2-coocoo
             └─ P3-cream-logo
                 └─ P4-speak-dedupe
                     └─ P5-json-unwrap
                         └─ P6-storm-fusion
                             └─ P7-afternoon
                                 └─ P8-emplates-mega
                                     └─ P9-reading-order
```

Independent stacks if needed: **P1** can land alone; **P5** can land anytime after P0 (low risk); **P8–P9** should not merge before speech kills if you want cleaner bug reports.

---

## Per-PR checklist template

```
Title: kill-list P#: <theme>
- [ ] Production delete/simplify only for this theme
- [ ] Tests updated (no assert requiring the one-off)
- [ ] Panel nicknames removed from touched comments
- [ ] SpeakRect.Tests green
- [ ] ModeSmoke green (if OCR/speech/geometry)
- [ ] Manual: ≥3 pages not the original archive
- [ ] Ticket filed for any known regression accepted by this kill
- [ ] speak-path-checklist / this kill-list updated if path changes
```

---

## Tracking table (fill as PRs land)

| PR | Status | Merged | Accepted regressions / tickets |
|----|--------|--------|--------------------------------|
| P0 | done (docs) | local | — |
| P1 | done | local | VL may speak `uchar` again |
| P2 | done (aggressive A) | local | repeated-syllable art OCR may speak |
| P3 | done | local | single-token logos on art may speak |
| P4 | done | local | short stem-reuse replies may drop as echoes |
| P5 | done (de-personalize) | local | unwrap kept; synthetic fixtures only |
| P6 | done | local | non-Latin spam / novel trunc-mush risk |
| P7 | done | local | tight crop supersets of short full units may lose opener words |
| P8 | done | local | mega islands may mid-panel without area full-frame branch |
| P9 | done | local | stack continuity / named-page order edges removed |

---

## Quick file index (for implementers)

| Theme | Primary files |
|-------|----------------|
| uchar | `SpeechTextRule.cs`, `SpeechCleaner.cs`, `tests/ModeSmoke/Program.cs` |
| coocoo | `ComicBestOfFusion.cs`, `OcrProcessor.cs` (dyn-fog + dead-island), unit tests |
| cream | `OcrProcessor.FilterDeadDetectRegions`, `GeometryPipelineTests.cs` |
| dedupe | `SpeechCleaner.DedupeSpeakUnitsForTts`, ModeSmoke |
| JSON | `SpeechCleaner` unwrap methods, ModeSmoke Adrienne block |
| Storm | `SpeechCleaner.IsMostlyNonLatinLetterNoise`, `ComicBestOfFusion.LooksLikeTruncatedOcrGarbage` |
| afternoon | `ComicBestOfFusion` full-order prefer crop, `MinSpeakUnitWords` comments |
| Emplate | `OcrProcessor` sequential mega branch, `ComicRegionGeometry.RegionIsNearFullFrame` |
| order | `ComicRegionGeometry.ApplyLightStackPreference` (+ nested helpers) |
