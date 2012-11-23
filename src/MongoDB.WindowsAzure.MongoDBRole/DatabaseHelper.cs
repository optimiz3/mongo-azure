/*
 * Copyright 2010-2012 10gen Inc.
 * file : ReplicaSetHelper.cs
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

    using MongoDB.Bson;
    using MongoDB.Bson.Serialization;
    using MongoDB.Driver;
    using MongoDB.WindowsAzure.Common;
    using MongoDB.WindowsAzure.Common.Bson;

    using System;
    using System.Net;
    using System.Threading;

    internal static class DatabaseHelper
    {
        private static readonly string AuthenticationRequired = "need to login";

        private const int RetryDelayMS = 400;

        /// <summary>
        /// Initialzes a replica set if enabled and provisions authentication if enabled.
        /// </summary>
        /// <param name="endPoint">The IPEndPoint of the MongoDB replica instance to provision from.</param>
        internal static void Initialize(IPEndPoint endPoint)
        {
            DatabaseHelper.EnsureMongodIsListening(endPoint);

            var authenticate = RoleSettings.Authenticate && 
                RoleSettings.AdminCredentials != null;

            if (RoleSettings.ReplicaSetName != null)
            {
                var primaryId = -1;

                bool replSetInit = false, addAdmin = false;

                var server = GetSlaveOkConnection(endPoint);
                var adminDatabase = server.GetDatabase(
                    "admin",
                    SafeMode.True);
                try
                {
                    try
                    {
                        var replSetStatus = BsonSerializer.Deserialize<ReplSetStatus>(
                            adminDatabase
                            .RunCommand("replSetGetStatus")
                            .Response);

                        primaryId = GetPrimaryId(replSetStatus);
                    }
                    catch (MongoAuthenticationException)
                    {
                        // Admin database needs to be provisioned
                        addAdmin = true;

                        adminDatabase = server.GetDatabase(
                            "admin",
                            null,
                            SafeMode.True);

                        var replSetStatus = BsonSerializer.Deserialize<ReplSetStatus>(
                            adminDatabase
                            .RunCommand("replSetGetStatus")
                            .Response);

                        primaryId = GetPrimaryId(replSetStatus);
                    }
                }
                catch (MongoCommandException e)
                {
                    if (e.CommandResult.ErrorMessage == AuthenticationRequired)
                    {
                        // Another instance provisioned authentication
                        return;
                    }

                    if (!TryGetIsReplSetInit(e, out replSetInit))
                    {
                        throw;
                    }
                }

                var instanceId = ConnectionUtilities.GetReplicaId(
                    RoleEnvironment.CurrentRoleInstance);

                if (replSetInit)
                {
                    if (instanceId == 0)
                    {
                        DatabaseHelper.ReplSetInit(
                            adminDatabase);
                    }
                }

                if (authenticate)
                {
                    if (primaryId == -1)
                    {
                        for (;;)
                        {
                            try
                            {
                                var replSetStatus = BsonSerializer.Deserialize<ReplSetStatus>(
                                    adminDatabase.RunCommand("replSetGetStatus").Response);

                                primaryId = GetPrimaryId(replSetStatus);

                                if (primaryId >= 0)
                                {
                                    break;
                                }
                            }
                            catch (MongoCommandException e)
                            {
                                if (e.CommandResult.ErrorMessage == AuthenticationRequired)
                                {
                                    // Another instance provisioned authentication
                                    return;
                                }

                                if (!TryGetIsReplSetInit(e, out replSetInit))
                                {
                                    throw;
                                }
                            }

                            Thread.Sleep(RetryDelayMS);
                        }
                    }

                    if (instanceId == primaryId)
                    {
                        if (addAdmin)
                        {
                            DatabaseHelper.ProvisionAdminUser(
                                endPoint.Port);
                        }

                        DatabaseHelper.ProvisionUsers();
                    }
                }
            }
            else
            {
                if (authenticate)
                {
                    var server = GetSlaveOkConnection(endPoint);
                    var adminDatabase = server.GetDatabase("admin");
                    try
                    {
                        adminDatabase.GetStats();
                    }
                    catch (MongoAuthenticationException)
                    {
                        DatabaseHelper.ProvisionAdminUser(
                            endPoint.Port);
                    }

                    DatabaseHelper.ProvisionUsers();
                }
            }
        }

        internal static MongoServer GetSlaveOkConnection(IPEndPoint endPoint)
        {
            return ConnectionUtilities.CreateServer(
                true,
                null,
                endPoint);
        }

        internal static void Shutdown(IPEndPoint endPoint)
        {
            using (DiagnosticsHelper.TraceMethod())
            {
                var server = DatabaseHelper.GetSlaveOkConnection(endPoint);
                server.Shutdown();
            }
        }
        
        internal static void Stepdown(IPEndPoint endPoint)
        {
            using (DiagnosticsHelper.TraceMethod())
            {
                var server = GetSlaveOkConnection(endPoint);
                if (server.State == MongoServerState.Disconnected)
                {
                    server.Connect();
                }

                if (server.Instance.IsPrimary)
                {
                    var stepDownCommand = new CommandDocument
                    {
                        {"replSetStepDown", 1}
                    };

                    try
                    {
                        server["admin"].RunCommand(stepDownCommand);
                    }
                    catch (System.IO.EndOfStreamException)
                    {
                        // replSetStepDown forces the client to disconnect
                        // http://docs.mongodb.org/manual/reference/command/replSetStepDown/#replSetStepDown
                    }
                }
            }
        }

        private static void EnsureMongodIsListening(IPEndPoint endPoint)
        {
            var server = GetSlaveOkConnection(endPoint);
            for (; ; )
            {
                try
                {
                    server.Connect(new TimeSpan(0, 0, 5));

                    break;
                }
                catch (MongoConnectionException e)
                {
                    DiagnosticsHelper.TraceInformation(e.Message);
                }

                Thread.Sleep(RetryDelayMS);
            }
        }

        private static int GetPrimaryId(ReplSetStatus replSetStatus)
        {
            foreach (var member in replSetStatus.Members)
            {
                if (member.State == ReplSetMemberState.Primary)
                {
                    return member.Id;
                }
            }

            return -1;
        }

        private static void ProvisionAdminUser(
            int port)
        {
            // admin provisoning is only allowed over a localhost connection
            var server = MongoServer.Create("mongodb://localhost:" + port + "/");

            var adminDatabase = server.GetDatabase(
                "admin",
                null,
                SafeMode.True);

            try
            {
                adminDatabase.AddUser(RoleSettings.AdminCredentials);
            }
            catch (MongoSafeModeException e)
            {
                // Thrown after authentication is enabled
                if (e.CommandResult.ErrorMessage != AuthenticationRequired)
                {
                    throw;
                }
            }
        }

        private static void ProvisionUsers()
        {
            var userCredentials = Settings.UserCredentials;
            var userDatabases = Settings.UserDatabases;

            if (userCredentials == null ||
                userDatabases == null ||
                userDatabases.Count == 0)
            {
                return;
            }

            // user provisioning must happen against the primary
            var server = ConnectionUtilities.CreateServer(false);

            var adminCredentials = RoleSettings.AdminCredentials;

            foreach (var databaseName in userDatabases)
            {
                // admin credentials are required for user provisioning
                var database = server.GetDatabase(
                    databaseName,
                    adminCredentials,
                    SafeMode.True);

                // always reprovision as the password may have changed
                database.AddUser(userCredentials);
            }
        }

        private static void ReplSetInit(
            MongoDatabase adminDatabase)
        {
            var currentInstance = RoleEnvironment.CurrentRoleInstance;

            var instances = RoleEnvironment.Roles[
                currentInstance.Role.Name].Instances;

            var membersDocument = new BsonArray();
            foreach (var instance in instances)
            {
                var replicaEndPoint = ConnectionUtilities.GetReplicaEndPoint(instance);

                if (instance != currentInstance)
                {
                    EnsureMongodIsListening(replicaEndPoint);
                }

                var replicaId = ConnectionUtilities.GetReplicaId(instance);

                membersDocument.Add(new BsonDocument {
                    {"_id", replicaId },
                    {"host", replicaEndPoint.ToString() }
                });
            }
            var cfg = new BsonDocument {
                { "_id", RoleSettings.ReplicaSetName },
                { "members", membersDocument }
            };
            var initCommand = new CommandDocument {
                { "replSetInitiate", cfg }
            };
            adminDatabase.RunCommand(initCommand);
        }

        private static bool TryGetIsReplSetInit(MongoCommandException e, out bool replSetInit)
        {
            BsonValue value;

            if (!e.CommandResult.Response.TryGetValue("startupStatus", out value))
            {
                replSetInit = false;

                return false;
            }

            replSetInit = (ReplSetStartupStatus)value.AsInt32 == ReplSetStartupStatus.ErrorConfig;

            return true;
        }
    }
}
