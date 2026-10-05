namespace Relatude.DB {
    public class Defaults {
        public const string AdminUrlRoot = "relatude.db";
        public const string SettingsFileName = "relatude.db.json";
        /// <summary>The folder below the content root that holds Relatude's settings files: <see cref="SettingsFileName"/>
        /// itself, the settings of the database without a short name (logs/, datamodel.overrides.json), and a folder for each database
        /// with one (see Settings.DatabaseShortName). Older versions kept relatude.db.json at the content root
        /// itself, and for a while in relatude.settings/db/; the server moves it here at start.</summary>
        public const string SettingsFolderPath = "relatude.settings";
        /// <summary>relatude.settings/relatude.db.json: where the settings file is, relative to the content root.</summary>
        public const string SettingsFilePath = SettingsFolderPath + "/" + SettingsFileName;
        public const string TempFolderPath = "relatude.db.temp";
        public const string DataFolderPath = "relatude.db";
        /// <summary>The Relatude.License server an installation reports in to and signs in through, unless its settings name another.</summary>
        public const string ServicesServerUrl = "https://services.relatude.com";
    }
}
