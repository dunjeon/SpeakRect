using Xunit;

// AppSettings.Current is a process-wide singleton. Several test classes mutate mode
// flags / POI / voice settings; parallel collections race and flake on CI
// (e.g. Poi_markers_default_off vs Entering_comic_book_via_mode_flag_…).
// Suite is small (~80 tests, ~1–2s) so full serial is fine.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
