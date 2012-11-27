/*
 * Copyright 2010-2012 10gen Inc.
 * file : MongoDBRole.cs
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
    using Microsoft.WindowsAzure;
    using Microsoft.WindowsAzure.ServiceRuntime;
    using Microsoft.WindowsAzure.StorageClient;

    using MongoDB.Driver;
    using MongoDB.WindowsAzure.Common;

    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Threading;

    public class MongoDBRole : RoleEntryPoint
    {
        private MongoDBProcess mongodProcess;
        private CloudDrive mongoDataDrive;
        private ManualResetEvent stopEvent;
        private string tempKeyFile;

        public override void Run()
        {
            using (DiagnosticsHelper.TraceMethod())
            {
                this.mongodProcess.WaitForExit();

                if (this.tempKeyFile != null)
                {
                    File.Delete(this.tempKeyFile);                    
                }

                if (!Settings.RecycleOnExit)
                {
                    this.stopEvent.WaitOne();
                }
            }
        }

        public override bool OnStart()
        {
            using (DiagnosticsHelper.TraceMethod())
            {
                // For information on handling configuration changes
                // see the MSDN topic at http://go.microsoft.com/fwlink/?LinkId=166357.

                // Set the maximum number of concurrent connections
                ServicePointManager.DefaultConnectionLimit = 12;

                CloudStorageAccount.SetConfigurationSettingPublisher((configName, configSetter) =>
                {
                    configSetter(RoleEnvironment.GetConfigurationSettingValue(configName));
                });

                RoleEnvironment.Changing += RoleEnvironmentChanging;
                RoleEnvironment.Changed += RoleEnvironmentChanged;

                DiagnosticsHelper.TraceInformation("ReplicaSetName='{0}'", RoleSettings.ReplicaSetName);

                StartMongoD();

                DatabaseHelper.Initialize(this.mongodProcess.EndPoint);

                this.stopEvent = new ManualResetEvent(false);
            }

            return true;
        }

        public override void OnStop()
        {
            using (DiagnosticsHelper.TraceMethod())
            {
                if (this.mongodProcess != null &&
                    this.mongodProcess.IsRunning)
                {
                    if (RoleSettings.ReplicaSetName != null)
                    {
                        try
                        {
                            DatabaseHelper.Stepdown(this.mongodProcess.EndPoint);
                        }
                        catch (MongoCommandException e)
                        {
                            // Ignore MongoCommandExceptions in OnStop
                            DiagnosticsHelper.TraceWarningException("Stepdown failed", e);
                        }
                    }

                    try
                    {
                        DatabaseHelper.Shutdown(this.mongodProcess.EndPoint);
                    }
                    catch (MongoCommandException e)
                    {
                        // Ignore MongoCommandExceptions in OnStop
                        DiagnosticsHelper.TraceWarningException("Shutdown failed", e);
                    }
                }

                if (this.mongoDataDrive != null)
                {
                    try
                    {
                        using (DiagnosticsHelper.TraceMethod("CloudDataDrive.Unmount"))
                        {
                            this.mongoDataDrive.Unmount();
                        }
                    }
                    catch (CloudDriveException e)
                    {
                        //Ignore CloudDriveException in OnStop
                        DiagnosticsHelper.TraceWarningException("Unmount failed", e);
                    }
                }

                if (this.stopEvent != null)
                {
                    this.stopEvent.Set();
                }
            }
        }

        private static string CreateKeyFile(
            string replicaSetKey)
        {
            var tempFileName = Path.GetTempFileName();

            using (var streamWriter = new StreamWriter(
                tempFileName))
            {
                streamWriter.Write(replicaSetKey);
            }

            return tempFileName;
        }

        private static CloudDrive MountCloudDrive()
        {
            var containerName = ConnectionUtilities.GetDataContainerName(
                RoleSettings.ReplicaSetName);

            var replicaId = ConnectionUtilities.GetReplicaId(
                RoleEnvironment.CurrentRoleInstance);
            var blobName = ConnectionUtilities.GetDataBlobName(replicaId);

            DiagnosticsHelper.TraceInformation("Mounting cloud drive as container \"{0}\" with blob \"{1}\"",
                containerName,
                blobName);

            var storageAccount = CloudStorageAccount.FromConfigurationSetting(
                Constants.MongoDataCredentialSetting);

            var blobClient = storageAccount.CreateCloudBlobClient();

            DiagnosticsHelper.TraceInformation("Get container");
            var driveContainer = blobClient.GetContainerReference(containerName);

            // create blob container (it has to exist before creating the cloud drive)
            try
            {
                driveContainer.CreateIfNotExist();
            }
            catch (StorageException e)
            {
                DiagnosticsHelper.TraceErrorException(
                    "Failed to create container",
                    e);
                throw;
            }

            var mongoBlobUri = blobClient
                .GetContainerReference(containerName)
                .GetPageBlobReference(blobName)
                .Uri;
            DiagnosticsHelper.TraceInformation("Blob uri obtained {0}", mongoBlobUri);

            // create the cloud drive
            var mongoDrive = storageAccount.CreateCloudDrive(mongoBlobUri.ToString());

            try
            {
                mongoDrive.CreateIfNotExist(Settings.DataDirSizeMB);
            }
            catch (StorageException e)
            {
                DiagnosticsHelper.TraceErrorException(
                    "Failed to create cloud drive",
                    e);
                throw;
            }

            DiagnosticsHelper.TraceInformation("Initialize cache");
            var localStorage = RoleEnvironment.GetLocalResource(
                Settings.LocalDataDirSetting);

            CloudDrive.InitializeCache(
                localStorage.RootPath.TrimEnd('\\'),
                localStorage.MaximumSizeInMegabytes);

            // mount the drive and get the root path of the drive it's mounted as
            try
            {
                using (DiagnosticsHelper.TraceMethod("CloudDrive.Mount"))
                {
                    mongoDrive.Mount(
                        localStorage.MaximumSizeInMegabytes,
                        DriveMountOptions.None);
                }
            }
            catch (CloudDriveException e)
            {
                DiagnosticsHelper.TraceErrorException(
                    "Failed to mount cloud drive",
                    e);
                throw;
            }

            return mongoDrive;
        }

        private void StartMongoD()
        {
            using (DiagnosticsHelper.TraceMethod())
            {
                var endPoints = ConnectionUtilities.GetReplicaEndPoints(
                    RoleEnvironment.CurrentRoleInstance);

                var mongodEndPoint = endPoints.First();

                DiagnosticsHelper.TraceInformation(
                    "Obtained host={0}, port={1}",
                    mongodEndPoint.Address,
                    mongodEndPoint.Port);

                var mongodPath = Path.Combine(
                    Environment.GetEnvironmentVariable("RoleRoot"),
                    Settings.MongoDBBinaryFolder,
                    "mongod.exe");

                var dbPath = GetMongoDataPath();

                var logPath = GetLogPath();

                var logLevel = Settings.MongodLogLevel;

                var mongodProcess = new MongoDBProcess(mongodPath);

                mongodProcess.Port = mongodEndPoint.Port;
                mongodProcess.DbPath = dbPath;
                mongodProcess.DirectoryPerDb = Settings.DirectoryPerDB;
                mongodProcess.LogLevel = Settings.MongodLogLevel;
                mongodProcess.LogPath = logPath;

                // Azure doesn't support IPv6 yet
                // https://www.windowsazure.com/en-us/support/faq/
                //mongodProcess.IPv6 = true;

                //mongodProcess.BindIp = endPoints.Select(endPoint => endPoint.Address).ToArray();

                mongodProcess.ReplSet = RoleSettings.ReplicaSetName;

                mongodProcess.Auth = RoleSettings.Authenticate;

                var replicaSetKey = Settings.ReplicaSetKey;
                if (replicaSetKey != null)
                {
                    this.tempKeyFile = CreateKeyFile(replicaSetKey);

                    mongodProcess.KeyFile = this.tempKeyFile;
                }

                if (RoleEnvironment.IsEmulated)
                {
                    mongodProcess.OpLogSizeMB = 100;
                    mongodProcess.SmallFiles = true;
                    mongodProcess.NoPreAlloc = true;
                }
                else
                {
                    mongodProcess.NoHttpInterface = true;
                    mongodProcess.LogAppend = true;
                }

                mongodProcess.Start();

                this.mongodProcess = mongodProcess;
            }
        }


        private string GetMongoDataPath()
        {
            using (DiagnosticsHelper.TraceMethod())
            {
                this.mongoDataDrive = MountCloudDrive();
                DiagnosticsHelper.TraceInformation("Mounted Azure drive as \"{0}\"", this.mongoDataDrive.LocalPath);

                var path = Path.Combine(this.mongoDataDrive.LocalPath, "data");
                Directory.CreateDirectory(path);
                return path;
            }
        }

        private static string GetLogPath()
        {
            using (DiagnosticsHelper.TraceMethod())
            {
                var localStorage = RoleEnvironment.GetLocalResource(Settings.LogDirSetting);
                return Path.Combine(localStorage.RootPath, Settings.MongodLogFileName);
            }
        }

        private void RoleEnvironmentChanging(object sender, RoleEnvironmentChangingEventArgs e)
        {
            Func<RoleEnvironmentConfigurationSettingChange, bool> changeIsExempt =
                x => !Settings.ExemptConfigurationItems.Contains(x.ConfigurationSettingName);
            var environmentChanges = e.Changes.OfType<RoleEnvironmentConfigurationSettingChange>();
            e.Cancel = environmentChanges.Any(changeIsExempt);
            DiagnosticsHelper.TraceInformation("Role config changing. Cancel set to {0}",
                e.Cancel);
        }

        private void RoleEnvironmentChanged(object sender, RoleEnvironmentChangedEventArgs e)
        {
            // Get the list of configuration setting changes
            var settingChanges = e.Changes.OfType<RoleEnvironmentConfigurationSettingChange>();

            foreach (var settingChange in settingChanges)
            {
                var settingName = settingChange.ConfigurationSettingName;

                DiagnosticsHelper.TraceInformation(
                    "Setting '{0}' now has value \"{1}\"",
                    settingName,
                    RoleEnvironment.GetConfigurationSettingValue(settingName));

                switch (settingName)
                {
                    case Settings.LogVerbositySetting:
                        var logLevel = Settings.GetLogLevel();
                        if (logLevel != Settings.MongodLogLevel)
                        {
                            Settings.MongodLogLevel = logLevel;

                            if (this.mongodProcess != null)
                            {
                                this.mongodProcess.LogLevel = logLevel;
                            }
                        }
                        break;

                    case Settings.RecycleOnExitSetting:
                        Settings.RecycleOnExit = Settings.GetRecycleOnExit();
                        break;
                }
            }

            // Get the list of topology changes
            var topologyChanges = e.Changes.OfType<RoleEnvironmentTopologyChange>();

            foreach (var topologyChange in topologyChanges)
            {
                var roleName = topologyChange.RoleName;
                DiagnosticsHelper.TraceInformation(
                    "Role {0} now has {1} instance(s)",
                    roleName,
                    RoleEnvironment.Roles[roleName].Instances.Count);
            }
        }
    }
}
