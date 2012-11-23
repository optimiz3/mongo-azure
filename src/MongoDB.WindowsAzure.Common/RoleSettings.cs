/*
 * Copyright 2010-2012 10gen Inc.
 * file : RoleSettings.cs
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

namespace MongoDB.WindowsAzure.Common
{
    using Microsoft.WindowsAzure.ServiceRuntime;

    using MongoDB.Driver;

    using System.Configuration;
    using System.Globalization;

    /// <summary>
    /// A shorthand class to fetch role environmental variables.
    /// </summary>
    public static class RoleSettings
    {
        /// <summary>
        /// The credentials provided to access the MongoDB storage account.
        /// </summary>
        public static string StorageCredentials
        {
            get
            {
                return RoleEnvironment.GetConfigurationSettingValue(Constants.MongoDataCredentialSetting);
            }
        }

        /// <summary>
        /// The name of the replica set MongoDB is configured to use. Defaults to "rs".
        /// </summary>
        public static string ReplicaSetName
        {
            get
            {
                var replicaSetName = RoleEnvironment.GetConfigurationSettingValue(
                    Constants.ReplicaSetNameSetting);

                if (replicaSetName.Length == 0)
                {
                    return null;
                }

                return replicaSetName;
            }
        }

        /// <summary>
        /// Gets whether authentication is enabled. Defaults to "false".
        /// </summary>
        public static bool Authenticate
        {
            get
            {
                var str = RoleEnvironment.GetConfigurationSettingValue(
                    Constants.AuthenticateSetting);

                if (str.Length == 0)
                {
                    return false;
                }

                bool value;

                if (!bool.TryParse(str, out value))
                {
                    ThrowInvalidConfigurationSetting(
                        Constants.AuthenticateSetting,
                        str);
                }

                return value;
            }
        }

        /// <summary>
        /// Gets the admin authentication credentials. Defaults to null.
        /// </summary>
        public static MongoCredentials AdminCredentials
        {
            get
            {
                var str = RoleEnvironment.GetConfigurationSettingValue(
                    Constants.AdminCredentialsSetting);

                if (str.Length == 0)
                {
                    return null;
                }

                var separator = str.IndexOf(':');

                if (separator < 0)
                {
                    ThrowInvalidConfigurationSetting(
                        Constants.AdminCredentialsSetting,
                        "<hidden>");
                }

                return new MongoCredentials(
                    str.Substring(0, separator),
                    str.Substring(separator + 1),
                    true);
            }
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
