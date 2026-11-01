namespace iSpyApplication
{
    // Which build this is. The CI build (.github/workflows/build.yml) overwrites this file with the
    // branch, commit and date; shown in Help > About and written to the log at startup.
    internal static class BuildInfo
    {
        public const string Description = "local build";
    }
}
