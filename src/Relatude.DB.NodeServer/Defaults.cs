namespace Relatude.DB {
    public class Defaults {
        public const string AdminUrlRoot = "relatude.db";
        public const string SettingsFileName = "relatude.db.json";
        /// <summary>The folder below the content root that holds Relatude's settings files - SETTINGS, part of the
        /// application: <see cref="SettingsFileName"/> itself, the settings of the database without a short name
        /// (logs/, graphql/, datamodel.json), and a folder for each database with one (see Settings.DatabaseShortName).
        /// Older versions kept relatude.db.json at the content root itself, and for a while in relatude.settings/db/;
        /// the server moves it here at start.</summary>
        public const string SettingsFolderPath = "relatude.settings";
        /// <summary>relatude.settings/relatude.db.json: where the settings file is, relative to the content root.</summary>
        public const string SettingsFilePath = SettingsFolderPath + "/" + SettingsFileName;
        /// <summary>The scratch folder below the content root, emptied at every start.</summary>
        public const string TempFolderPath = "relatude.data.temp";
        /// <summary>The folder below the content root a new installation keeps its databases in - DATA, this
        /// installation's: transaction log, indexes, files, and what the admin UI changed (overrides/). Older
        /// versions called it relatude.db; the server renames it at start (see Settings.DataFolderMigration).</summary>
        public const string DataFolderPath = "relatude.data";
        /// <summary>The Relatude.License server an installation reports in to and signs in through, unless its settings name another.</summary>
        public const string ServicesServerUrl = "https://services.relatude.com";
    }
}
