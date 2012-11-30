/*
 * Copyright 2010-2012 10gen Inc.
 * file : Settings.cs
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 * 
 * http://www.apache.org/licenses/LICENSE-2.0
 * 
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace MongoDB.WindowsAzure.MongoDBRole
{
    using Microsoft.WindowsAzure.ServiceRuntime;

    using MongoDB.WindowsAzure.Common;
    using MongoDB.Driver;

    using System;
    using System.Collections.ObjectModel;
    using System.Configuration;
    using System.Globalization;
    using System.Text.RegularExpressions;

    internal static class Settings
    {
        #region DO NOT MODIFY

        // configuration setting names
        // TODO move any shared ones to CommonSettings
        internal const string LocalDataDirSetting = "MongoDBLocalDataDir";
        internal const string DataDirSizeMBSetting = "MongoDBDataDirSizeMB";
        internal const string LogDirSetting = "MongodLogDir";
        internal const string LogVerbositySetting = "MongoDBLogVerbosity";
        internal const string DirectoryPerDBSetting = "MongoDBDirectoryPerDB";
        internal const string RecycleOnExitSetting = "RecycleOnExit";
        internal const string ReplicaSetKeySetting = "ReplicaSetKey";
        internal const string UserDatabasesSetting = "UserDatabases";
        internal const string LogFileEnableSetting = "LogFileEnable";

        internal const string MongoDBBinaryFolder = @"approot\MongoDBBinaries\bin";
        internal const string MongodLogFileName = "mongod.log";

        internal static readonly string[] ExemptConfigurationItems =
            new[] { LogVerbositySetting, RecycleOnExitSetting };


        // Default values for configurable settings
        private const int DefaultEmulatedDBDriveSize = 1024; // in MB
        private const int DefaultDeployedDBDriveSize = 100 * 1024; // in MB

        #endregion DO NOT MODIFY

        internal static readonly int DataDirSizeMB = GetDataDirSizeMB(); // in MB
        internal static int MongodLogLevel = GetLogLevel();
        internal static bool RecycleOnExit = GetRecycleOnExit();
        internal static readonly bool LogFileEnable = GetLogFileEnable();
        internal static readonly string ReplicaSetKey = GetReplicaSetKey();
        internal static readonly bool DirectoryPerDB = GetDirectoryPerDB();
        internal static readonly ReadOnlyCollection<string> UserDatabases = GetDatabases(UserDatabasesSetting);

        internal static bool GetRecycleOnExit()
        {
            return GetBooleanSetting(RecycleOnExitSetting, true);
        }

        internal static int GetLogLevel()
        {
            string value;

            if (!TryGetRoleConfigurationSettingValue(
                Settings.LogVerbositySetting,
                out value))
            {
                return 1;
            }

            var m = Regex.Match(value, "^-?(v{1,6})$");
            if (!m.Success)
            {
                ThrowInvalidConfigurationSetting(
                    Settings.LogVerbositySetting,
                    value);
            }

            return m.Groups[1].Value.Length - 1;
        }

        private static ReadOnlyCollection<string> GetDatabases(
            string configurationSettingName)
        {
            string value;

            if (!TryGetRoleConfigurationSettingValue(
                configurationSettingName,
                out value))
            {
                return null;
            }

            return Array.AsReadOnly(value.Split(','));
        }

        private static int GetDataDirSizeMB()
        {
            int dataDirSizeMB;

            if (!TryGetRoleConfigurationSettingValue(
                    DataDirSizeMBSetting,
                    out dataDirSizeMB))
            {
                dataDirSizeMB = RoleEnvironment.IsEmulated ?
                    DefaultEmulatedDBDriveSize : DefaultDeployedDBDriveSize;
            }

            return dataDirSizeMB;
        }

        private static bool GetDirectoryPerDB()
        {
            return GetBooleanSetting(
                Settings.DirectoryPerDBSetting,
                false);
        }

        private static bool GetLogFileEnable()
        {
            bool value;

            if (!TryGetRoleConfigurationSettingValue(
                Settings.LogFileEnableSetting,
                out value))
            {
                return true;
            }

            return value;
        }

        private static string GetReplicaSetKey()
        {
            string value;

            if (!TryGetRoleConfigurationSettingValue(
                Settings.ReplicaSetKeySetting,
                out value))
            {
                return null;
            }

            // values must be base64
            var m = Regex.Match(value, "^[0-9A-Za-z+/]+={0,2}$");
            if (!m.Success)
            {
                ThrowInvalidConfigurationSetting(
                    Settings.ReplicaSetKeySetting,
                    value);
            }

            return value;
        }

        private static bool GetBooleanSetting(
            string configurationSettingName,
            bool defaultValue)
        {
            bool value;

            if (!TryGetRoleConfigurationSettingValue(
                configurationSettingName,
                out value))
            {
                return defaultValue;
            }

            return value;
        }

        private static bool TryGetRoleConfigurationSettingValue(
            string configurationSettingName,
            out string value)
        {
            try
            {
                value = RoleEnvironment.GetConfigurationSettingValue(configurationSettingName);
            }
            catch (RoleEnvironmentException)
            {
                value = null;

                return false;
            }

            if (value.Length == 0)
            {
                value = null;

                return false;
            }

            return true;
        }

        private static bool TryGetRoleConfigurationSettingValue(
            string configurationSettingName,
            out bool value)
        {
            string str;

            if (!TryGetRoleConfigurationSettingValue(
                configurationSettingName,
                out str))
            {
                value = false;

                return false;
            }

            if (!bool.TryParse(str, out value))
            {
                ThrowInvalidConfigurationSetting(
                    configurationSettingName,
                    str);
            }

            return true;
        }

        private static bool TryGetRoleConfigurationSettingValue(
            string configurationSettingName,
            out int value)
        {
            string str;

            if (!TryGetRoleConfigurationSettingValue(
                configurationSettingName,
                out str))
            {
                value = 0;

                return false;
            }

            if (!int.TryParse(
                str,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value))
            {
                ThrowInvalidConfigurationSetting(
                    configurationSettingName,
                    str);
            }

            return true;
        }

        private static void ThrowInvalidConfigurationSetting(
            string name,
            string value)
        {
            var message = string.Format(
                CultureInfo.InvariantCulture,
                "Configuration setting name '{0}' has invalid value \"{1}\"",
                name,
                value);

            throw new ConfigurationErrorsException(message);
        }
    }
}
