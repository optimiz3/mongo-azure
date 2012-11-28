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
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Text.RegularExpressions;
    using System.Threading;

    internal static class DatabaseHelper
    {
        private const int MongoDBErrorCodeUnauthorized = 10057;

        private const int MongoDBErrorCodeReplicasDown = 13144;

        private static readonly string AuthenticationRequired = "need to login";

        private const int RetryDelayMS = 1000;

        /// <summary>
        /// Initialzes a replica set if enabled and provisions authentication if enabled.
        /// </summary>
        /// <param name="endPoint">The IPEndPoint of the MongoDB replica instance to provision from.</param>
        internal static void Initialize(IPEndPoint endPoint)
        {
            DatabaseHelper.EnsureMongodIsListening(endPoint);

            ProvisionReplicaSet(endPoint);

            ProvisionAuthenticate(endPoint.Port);
        }

        internal static void Reinitialize(IPEndPoint endPoint)
        {
            if (RoleSettings.ReplicaSetName == null ||
                !IsCurrentLowestReplicaId())
            {
                return;
            }

            var server = GetSlaveOkConnection(endPoint);

            var replSetStatus = GetReplSetStatus(server, true);

            if (GetTopologyChanged(replSetStatus))
            {
                ReplSetReconfig(endPoint);
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

        #region Provision Replica Set Members

        private static void ProvisionReplicaSet(IPEndPoint endPoint)
        {
            if (RoleSettings.ReplicaSetName == null || !IsCurrentLowestReplicaId())
            {
                return;
            }

            var server = GetSlaveOkConnection(endPoint);

            ReplSetStatus replSetStatus;

            for (; ; )
            {
                try
                {
                    replSetStatus = GetReplSetStatus(server, true);

                    break;
                }
                catch (MongoCommandException e)
                {
                    bool replSetInit;

                    if (!TryGetReplSetInit(e, out replSetInit))
                    {
                        throw;
                    }


                    if (replSetInit)
                    {
                        replSetStatus = null;

                        break;
                    }
                }

                Thread.Sleep(RetryDelayMS);
            }

            if (replSetStatus == null)
            {
                DatabaseHelper.ReplSetInit(endPoint);
            }
            else if (GetTopologyChanged(replSetStatus))
            {
                ReplSetReconfig(endPoint);
            }
        }

        #endregion

        #region Provision Authentication Members

        private static void ProvisionAuthenticate(
            int port)
        {
            var authenticate = RoleSettings.Authenticate &&
                RoleSettings.AdminCredentials != null;

            if (!authenticate)
            {
                return;
            }

            // Admin provisoning is only allowed over a localhost connection
            var server = MongoServer.Create(
                new MongoServerSettings
                {
                    Servers = new MongoServerAddress[]
                    {
                        new MongoServerAddress("localhost", port),
                    },
                    SlaveOk = true,
                    SafeMode = SafeMode.True,
                });

            if (RoleSettings.ReplicaSetName != null)
            {
                var primaryId = WaitForPrimary(server);

                var currentId = ConnectionUtilities.GetReplicaId(
                    RoleEnvironment.CurrentRoleInstance);

                if (currentId != primaryId)
                {
                    return;
                }
            }

            DatabaseHelper.ProvisionAdminUser(server);

            DatabaseHelper.ProvisionUsers(server);
        }

        private static void ProvisionAdminUser(
            MongoServer localhostServer)
        {
            var adminDatabase = localhostServer.GetDatabase(
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
            catch (MongoQueryException e)
            {
                var queryResult = e.QueryResult;

                // Verify already provisioned
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
            MongoServer server)
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

        #endregion

        #region WaitForPrimary Members

        private static int WaitForPrimary(
            MongoServer server)
        {
            var adminDatabase = server.GetDatabase("admin");

            var authenticate = RoleSettings.Authenticate &&
                RoleSettings.AdminCredentials != null;

            // connection is authenticated by default
            int primaryId;

            for (; ; )
            {
                try
                {
                    var replSetStatus = GetReplSetStatus(server, false);

                    primaryId = GetPrimaryReplicaId(replSetStatus);

                    if (primaryId >= 0)
                    {
                        break;
                    }
                }
                catch (MongoCommandException e)
                {
                    bool replSetInit;

                    // Another instance may be initializing the replica set
                    if (!TryGetReplSetInit(e, out replSetInit))
                    {
                        throw;
                    }
                }

                Thread.Sleep(RetryDelayMS);
            }

            return primaryId;
        }

        private static int GetPrimaryReplicaId(ReplSetStatus replSetStatus)
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

        #endregion

        #region Topology Members

        private static bool GetTopologyChanged(ReplSetStatus replSetStatus)
        {
            var replSetMembers = replSetStatus.Members;

            if (replSetMembers == null)
            {
                return true;
            }

            var instances = GetMongoDBRoleInstances();

            if (replSetMembers.Count != instances.Count)
            {
                return true;
            }

            var memberEndPoints = new Dictionary<int, IPEndPoint>();

            foreach (var replSetMember in replSetMembers)
            {
                IPEndPoint replicaEndPoint;

                if (!TryParseIPEndpoint(replSetMember.Name, out replicaEndPoint))
                {
                    var message = string.Format(
                        CultureInfo.InvariantCulture,
                        "Replica set member '{0}' name \"{1}\" is not a valid IPEndPoint",
                        replSetMember.Id,
                        replSetMember.Name);

                    throw new FormatException(message);
                }

                memberEndPoints.Add(replSetMember.Id, replicaEndPoint);
            }

            foreach (var instance in instances)
            {
                var replicaEndPoint = ConnectionUtilities.GetReplicaEndPoint(instance);

                var replicaId = ConnectionUtilities.GetReplicaId(instance);

                IPEndPoint memberEndPoint;

                if (!memberEndPoints.TryGetValue(replicaId, out memberEndPoint))
                {
                    return true;
                }

                // IPEndPoint implements object.Equals, but does NOT
                // implement the == and != operators. This means == and !=
                // will return reference comparison results instead of member
                // comparison results.
                if (!replicaEndPoint.Equals(memberEndPoint))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseIPEndpoint(
            string str,
            out IPEndPoint endPoint)
        {
            var m = Regex.Match(
                str,
                "^(?:\\[(?<IPAddress>[^\\]]+)\\]|(?<IPAddress>[^:\\]]+)):(?<Port>\\d{1,5})$");

            if (!m.Success)
            {
                endPoint = null;

                return false;
            }

            IPAddress address;

            if (!IPAddress.TryParse(
                m.Groups["IPAddress"].Value,
                out address))
            {
                endPoint = null;

                return false;
            }

            int port;

            if (!int.TryParse(
                m.Groups["Port"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out port))
            {
                endPoint = null;

                return false;
            }

            endPoint = new IPEndPoint(address, port);

            return true;
        }

        #endregion

        #region Command Members

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

        private static CommandResult RunAdminCommand(
            MongoServer server,
            CommandDocument command,
            bool defaultAuthenticate)
        {
            var authenticate = RoleSettings.Authenticate &&
                RoleSettings.AdminCredentials != null;

            MongoDatabase adminDatabase;

            if (!authenticate)
            {
                adminDatabase = server.GetDatabase("admin");
            }
            else
            {
                var adminCredentials = defaultAuthenticate ?
                    RoleSettings.AdminCredentials : null;

                adminDatabase = server.GetDatabase("admin", adminCredentials);
            }

            CommandResult commandResult;

            for (; ; )
            {
                try
                {
                    commandResult = adminDatabase.RunCommand(command);
                }
                catch (MongoAuthenticationException)
                {
                    if (!authenticate)
                    {
                        throw;
                    }

                    // Handle the case where the authentication has not yet or just been provisioned
                    var adminCredentials = adminDatabase.Credentials == null ?
                        RoleSettings.AdminCredentials : null;

                    adminDatabase = server.GetDatabase("admin", adminCredentials);

                    continue;
                }
                catch (MongoCommandException e)
                {
                    if (e.CommandResult.ErrorMessage == AuthenticationRequired)
                    {
                        if (!authenticate)
                        {
                            throw;
                        }

                        // Handle the case where the authentication has not yet or just been provisioned
                        var adminCredentials = adminDatabase.Credentials == null ?
                            RoleSettings.AdminCredentials : null;

                        adminDatabase = server.GetDatabase("admin", adminCredentials);

                        continue;
                    }

                    throw;
                }

                break;
            }

            return commandResult;
        }

        #endregion

        #region Replica Set Members

        private static BsonDocument BuildReplSetConfigDocument(int version)
        {
            var membersDocument = new BsonArray();
            foreach (var instance in GetMongoDBRoleInstances())
            {
                var replicaEndPoint = ConnectionUtilities.GetReplicaEndPoint(instance);

                var replicaId = ConnectionUtilities.GetReplicaId(instance);

                membersDocument.Add(new BsonDocument {
                    {"_id", replicaId },
                    {"host", replicaEndPoint.ToString() }
                });
            }
            var config = new BsonDocument {
                { "_id", RoleSettings.ReplicaSetName },
                { "members", membersDocument }
            };
            if (version > 0)
            {
                config.Add("version", version);
            }
            return config;
        }

        private static ReplSetStatus GetReplSetStatus(
            MongoServer server,
            bool defaultAuthenticate)
        {
            var command = new CommandDocument
            {
                { "replSetGetStatus", 1 },
            };

            var commandResult = RunAdminCommand(
                server,
                command,
                defaultAuthenticate);

            return BsonSerializer.Deserialize<ReplSetStatus>(
                commandResult.Response);
        }

        private static void ReplSetInit(
            IPEndPoint endPoint)
        {
            var currentInstance = RoleEnvironment.CurrentRoleInstance;

            foreach (var instance in GetMongoDBRoleInstances())
            {
                var replicaEndPoint =
                    ConnectionUtilities.GetReplicaEndPoint(instance);

                if (instance != currentInstance)
                {
                    EnsureMongodIsListening(replicaEndPoint);
                }
            }

            var server = GetSlaveOkConnection(endPoint);

            var initCommand = new CommandDocument {
                { "replSetInitiate", BuildReplSetConfigDocument(0) }
            };

            RunAdminCommand(server, initCommand, false);
        }

        private static void ReplSetReconfig(
            IPEndPoint endPoint)
        {
            var currentInstance = RoleEnvironment.CurrentRoleInstance;

            var instances = RoleEnvironment.Roles[
                currentInstance.Role.Name].Instances;

            foreach (var instance in instances)
            {
                var replicaEndPoint = ConnectionUtilities.GetReplicaEndPoint(instance);

                if (instance != currentInstance)
                {
                    EnsureMongodIsListening(replicaEndPoint);
                }
            }

            var server = GetSlaveOkConnection(endPoint);

            // Always force reconfiguration because the Azure RoleEnvironment
            // is authoritative. This handles the case where all IPs change
            // making the primary unreachable by other replicas.
            var version = 1;

            for (var versionRetryCount = 0; versionRetryCount < 2; )
            {
                try
                {
                    var reconfigCommand = new CommandDocument
                    {
                        { "replSetReconfig", BuildReplSetConfigDocument(version) },
                        { "force", true },
                    };

                    RunAdminCommand(server, reconfigCommand, true);
                }
                catch (MongoCommandException e)
                {
                    var response = e.CommandResult.Response;

                    BsonValue errmsg;

                    if (!response.TryGetValue("errmsg", out errmsg))
                    {
                        throw;
                    }

                    var m = Regex.Match(
                        (string)errmsg,
                        "version number must increase, old: (?<Old>\\d+) new: (?<New>\\d+)");

                    if (m.Success)
                    {
                        version = int.Parse(
                            m.Groups["Old"].Value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture) + 1;

                        ++versionRetryCount;

                        continue;
                    }

                    BsonValue errorCode;

                    // Wait for replicas to come online
                    if (response.TryGetValue("assertionCode", out errorCode) &&
                        (int)errorCode == MongoDBErrorCodeReplicasDown)
                    {
                        Thread.Sleep(RetryDelayMS);

                        continue;
                    }

                    throw;
                }
                catch (EndOfStreamException)
                {
                    // replSetReconfig can cause a disconnection
                }

                break;
            }
        }

        #endregion

        private static ReadOnlyCollection<RoleInstance> GetMongoDBRoleInstances()
        {
            return RoleEnvironment.Roles[
                RoleEnvironment.CurrentRoleInstance.Role.Name].Instances;
        }

        // returns true if the current replica has the lowest replica id
        private static bool IsCurrentLowestReplicaId()
        {
            var currentId = ConnectionUtilities.GetReplicaId(
                RoleEnvironment.CurrentRoleInstance);

            foreach (var instance in GetMongoDBRoleInstances())
            {
                var replicaId = ConnectionUtilities.GetReplicaId(instance);

                if (replicaId < currentId)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryGetReplSetInit(MongoCommandException e, out bool replSetInit)
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
