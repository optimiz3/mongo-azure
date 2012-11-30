/*
 * Copyright 2010-2012 10gen Inc.
 * file : DatabaseHelper.cs
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
    using MongoDB.Driver;
    using MongoDB.WindowsAzure.Common;

    using System;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Net;
    using System.Threading;

    internal static class DatabaseHelper
    {
        private const int MongoDBErrorCodeUnauthorized = 10057;

        private const int MongoDBErrorCodeNotMaster = 10054;

        private static readonly string AuthenticationRequired = "need to login";

        private const int RetryDelayMS = 1000;

        /// <summary>
        /// Initialzes a replica set if enabled and provisions authentication if enabled.
        /// </summary>
        /// <param name="endPoint">The IPEndPoint of the MongoDB replica instance to provision from.</param>
        internal static ReplSetHelper Initialize(IPEndPoint endPoint)
        {
            DatabaseHelper.EnsureMongodIsListening(endPoint);

            var adminHelper = new AdminHelper(endPoint.Port, true);

            var replSetHelper = ProvisionReplicaSet(adminHelper);

            ProvisionAuthenticate(replSetHelper);

            return replSetHelper;
        }

        internal static MongoServer GetSlaveOkConnection(IPEndPoint endPoint)
        {
            return ConnectionUtilities.CreateServer(
                true,
                null,
                endPoint);
        }

        internal static void Shutdown(AdminHelper adminHelper, bool force)
        {
            using (DiagnosticsHelper.TraceMethod())
            {
                var shutdownCommand = new CommandDocument
                {
                    { "shutdown", 1 },
                };

                if (force)
                {
                    shutdownCommand.Add("force", true);
                }

                try
                {
                    adminHelper.RunCommand(
                        shutdownCommand,
                        true);
                }
                catch (EndOfStreamException)
                {
                    // shutdown forces the client to disconnect
                }
            }
        }

        internal static void Stepdown(ReplSetHelper replSetHelper)
        {
            using (DiagnosticsHelper.TraceMethod())
            {
                if (replSetHelper.IsPrimary)
                {
                    var adminHelper = replSetHelper.AdminHelper;

                    var replSetStepDownCommand = new CommandDocument
                    {
                        { "replSetStepDown", 1 },
                    };

                    try
                    {
                        adminHelper.RunCommand(
                            replSetStepDownCommand,
                            true);
                    }
                    catch (EndOfStreamException)
                    {
                        // replSetStepDown forces the client to disconnect
                        // http://docs.mongodb.org/manual/reference/command/replSetStepDown/#replSetStepDown
                    }
                }
            }
        }

        private static ReplSetHelper ProvisionReplicaSet(
            AdminHelper adminHelper)
        {
            var replSetHelper = new ReplSetHelper(adminHelper);

            replSetHelper.Initialize();

            return replSetHelper;
        }

        private static void ProvisionAuthenticate(
            ReplSetHelper replSetHelper)
        {
            var adminHelper = replSetHelper.AdminHelper;

            if (adminHelper.AuthState == AdminAuthState.None)
            {
                return;
            }

            if (!replSetHelper.IsPrimary)
            {
                return;
            }

            DatabaseHelper.ProvisionAdminUser(adminHelper);

            DatabaseHelper.ProvisionUsers(adminHelper);
        }

        private static void ProvisionAdminUser(
            AdminHelper adminHelper)
        {
            if (adminHelper.AuthState == AdminAuthState.Unknown)
            {
                // determine the auth state
                var dbstatsCommand = new CommandDocument
                {
                    { "dbstats", 1 },
                };

                adminHelper.RunAdminCommand(dbstatsCommand);
            }

            var adminDatabase = adminHelper.GetAdminDatabase();

            try
            {
                adminDatabase.AddUser(RoleSettings.AdminCredentials);
            }
            catch (WriteConcernException e)
            {
                var commandResult = e.CommandResult;

                // thrown after authentication is enabled
                if (commandResult.ErrorMessage == AuthenticationRequired)
                {
                    adminHelper.NotifyAuthEnabled();
                }
                else
                {
                    throw;
                }
            }
            catch (MongoQueryException e)
            {
                var queryResult = e.QueryResult;

                // verify already provisioned
                BsonValue errorCode;

                if (!queryResult.TryGetValue(
                    "code",
                    out errorCode) ||
                    (int)errorCode != MongoDBErrorCodeUnauthorized)
                {
                    throw;
                }
            }
        }

        private static void ProvisionUsers(
            AdminHelper adminHelper)
        {
            var userCredentials = RoleSettings.UserCredentials;
            var userDatabases = Settings.UserDatabases;

            if (userCredentials == null ||
                userDatabases == null ||
                userDatabases.Count == 0)
            {
                return;
            }

            // user provisioning must happen against the primary
            foreach (var databaseName in userDatabases)
            {
                // admin credentials are required for user provisioning
                var database = adminHelper.GetDatabase(
                    databaseName);

                // always reprovision as the password may have changed
                database.AddUser(userCredentials);
            }
        }

        internal static void EnsureMongodIsListening(IPEndPoint endPoint)
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
                catch (IOException e)
                {
                    DiagnosticsHelper.TraceInformation(e.Message);
                }

                Thread.Sleep(RetryDelayMS);
            }
        }

        internal static ReadOnlyCollection<RoleInstance> GetMongoDBRoleInstances()
        {
            return RoleEnvironment.Roles[
                RoleEnvironment.CurrentRoleInstance.Role.Name].Instances;
        }
    }
}
