namespace NomadModManager
{
    /// <summary>
    /// Which build this is. The Nexus edition (built with -p:NexusEdition=true) keeps to Nexus Mods' file rules: it only
    /// talks to Nexus Mods (to browse and check for updates), never downloads files or programs by itself, never updates
    /// itself, and has no website tab, account, packs or links to other sites. Nexus mods are downloaded by the player in
    /// their own web browser, and the menu installs the file once it lands in their Downloads folder.
    /// </summary>
    internal static class Edition
    {
#if NEXUS_EDITION
        public const bool Nexus = true;
#else
        public const bool Nexus = false;
#endif
        public const string BepInExPage = "https://www.nexusmods.com/nomaddrive/mods/7";
    }
}
