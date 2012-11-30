/*
 * Copyright 2010-2012 10gen Inc.
 * file : ReplSetHelper.cs
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
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text.RegularExpressions;
    using System.Threading;

    internal class ReplSetHelper
    {
        private const int MongoDBErrorCodeReplicasDown = 13144;

        private const int RetryDelayMS = 1000;

        private const int UpdateIntervalMS = 15000;

        private readonly AdminHelper adminHelper;

        private readonly int currentId;

        private readonly bool enabled;

        private int primaryId;

        private Dictionary<int, IPEndPoint> topology;

        private Thread thread;

        private ManualResetEvent threadEvent;

        internal ReplSetHelper(AdminHelper adminHelper)
        {
            this.adminHelper = adminHelper;

            this.currentId = ConnectionUtilities.GetReplicaId(
                RoleEnvironment.CurrentRoleInstance);

            this.enabled = RoleSettings.ReplicaSetName != null;

            this.primaryId = -1;
        }

        internal AdminHelper AdminHelper
        {
            get
            {
                return this.adminHelper;
            }
        }

        internal bool IsPrimary
        {
            get
            {
                return !this.enabled ||
                    this.currentId == this.primaryId;
            }
        }

        internal int ReplicaCount
        {
            get
            {
                if (this.topology == null)
                {
                    return 0;
                }

                return this.topology.Count;
            }
        }

        /// <summary>
        /// Initialzes a replica set if enabled and waits for a primary.
        /// </summary>
        /// <param name="endPoint">The IPEndPoint of the MongoDB replica instance to provision from.</param>
        internal void Initialize()
        {
            if (!this.enabled)
            {
                return;
            }

            ReplSetStatus replSetStatus;

            for (; ; )
            {
                try
                {
                    replSetStatus = GetReplSetStatus(this.adminHelper);

                    // Store the active topology.
                    this.topology = GetTopologyFromReplSetStatus(
                        replSetStatus);

                    this.primaryId = GetPrimaryReplicaId(
                        replSetStatus,
                        false);

                    if (this.primaryId >= 0)
                    {
                        break;
                    }

                    this.UpdateTopology();
                }
                catch (MongoCommandException e)
                {
                    BsonValue startupStatus;

                    if (!e.CommandResult.Response.TryGetValue("startupStatus", out startupStatus))
                    {
                        throw;
                    }

                    if ((ReplSetStartupStatus)startupStatus.AsInt32 == ReplSetStartupStatus.ErrorConfig &&
                        this.HasLowestReplicaId())
                    {
                        ReplSetInit(this.adminHelper);
                    }
                }
                catch (IOException)
                {
                    // Thrown when the replSet gets reconfigured.
                }

                Thread.Sleep(RetryDelayMS);
            }
        }

        internal void StartInstanceMaintainer()
        {
            if (!this.enabled)
            {
                return;
            }

            if (this.thread != null)
            {
                return;
            }

            this.thread = new Thread(() =>
            {
                using (DiagnosticsHelper.TraceMethod("InstanceMaintainerThread"))
                {
                    for (; ; )
                    {
                        if (this.threadEvent.WaitOne(UpdateIntervalMS))
                        {
                            break;
                        }

                        try
                        {
                            this.UpdateTopology();
                        }
                        catch (MongoCommandException e)
                        {
                            DiagnosticsHelper.TraceWarningException(null, e);
                        }
                        catch (IOException e)
                        {
                            DiagnosticsHelper.TraceWarningException(null, e);
                        }
                    }
                }
            })
            {
                Name = "Instance Maintainer Thread",
            };

            this.threadEvent = new ManualResetEvent(false);

            this.thread.Start();
        }

        internal void StopInstanceMaintainer()
        {
            if (this.thread == null)
            {
                return;
            }

            this.threadEvent.Set();

            this.thread.Join();

            this.threadEvent.Close();

            this.threadEvent = null;

            this.thread = null;
        }

        private void UpdateTopology()
        {
            if (this.topology == null)
            {
                throw new InvalidOperationException();
            }

            var topology = GetTopologyFromRoleInstances(
                DatabaseHelper.GetMongoDBRoleInstances());

            if (TopologyEquals(this.topology, topology))
            {
                return;
            }

            // Resync the topology from the node.
            var replSetStatus = GetReplSetStatus(this.adminHelper);

            // Store the active topology.
            this.topology = GetTopologyFromReplSetStatus(
                replSetStatus);

            this.primaryId = GetPrimaryReplicaId(
                replSetStatus,
                false);

            if (TopologyEquals(this.topology, topology))
            {
                return;
            }

            // Allow topology updates if no quorum can be reached.
            // This can happen where the number of instances has been reduced
            // or a majority of instance endpoints have been reassigned.
            var primaryId = GetPrimaryReplicaId(
                replSetStatus,
                true);

            // Only update the topology if the node is primary.
            if (this.currentId != primaryId)
            {
                return;
            }

            ReplSetReconfig(
                this.adminHelper,
                this.primaryId != primaryId);

            // store the active topology
            this.topology = topology;
        }

        #region Topology Helpers

        private static Dictionary<int, IPEndPoint> GetTopologyFromRoleInstances(
            IEnumerable<RoleInstance> roleInstances)
        {
            return roleInstances.ToDictionary(
                roleInstance => ConnectionUtilities.GetReplicaId(roleInstance),
                roleInstance => ConnectionUtilities.GetReplicaEndPoint(roleInstance));
        }

        private static Dictionary<int, IPEndPoint> GetTopologyFromReplSetStatus(
            ReplSetStatus replSetStatus)
        {
            var replSetMembers = replSetStatus.Members;

            if (replSetMembers == null)
            {
                return new Dictionary<int, IPEndPoint>();
            }

            return replSetMembers.ToDictionary(
                replSetMember => replSetMember.Id,
                replSetMember =>
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

                    return replicaEndPoint;
                });
        }

        private static bool TopologyEquals(
            Dictionary<int, IPEndPoint> x,
            Dictionary<int, IPEndPoint> y)
        {
            if (object.ReferenceEquals(x, y))
            {
                return true;
            }

            if (x == null || y == null)
            {
                return false;
            }

            if (x.Count != y.Count)
            {
                return false;
            }

            foreach (var item in y)
            {
                IPEndPoint endPoint;

                if (!x.TryGetValue(item.Key, out endPoint))
                {
                    return false;
                }

                // IPEndPoint implements object.Equals, but does NOT
                // implement the == and != operators. This means == and !=
                // will return reference comparison results instead of member
                // comparison results.
                if (!item.Value.Equals(endPoint))
                {
                    return false;
                }
            }

            return true;
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

        #region ReplSet Commands

        private static ReplSetStatus GetReplSetStatus(
            AdminHelper adminHelper)
        {
            var command = new CommandDocument
            {
                { "replSetGetStatus", 1 },
            };

            var commandResult = adminHelper.RunAdminCommand(command);

            return BsonSerializer.Deserialize<ReplSetStatus>(
                commandResult.Response);
        }

        private static void ReplSetInit(
            AdminHelper adminHelper)
        {
            var currentInstance = RoleEnvironment.CurrentRoleInstance;

            foreach (var instance in DatabaseHelper.GetMongoDBRoleInstances())
            {
                var replicaEndPoint =
                    ConnectionUtilities.GetReplicaEndPoint(instance);

                if (instance != currentInstance)
                {
                    DatabaseHelper.EnsureMongodIsListening(replicaEndPoint);
                }
            }

            var initCommand = new CommandDocument {
                { "replSetInitiate", BuildReplSetConfigDocument(0) }
            };

            adminHelper.RunAdminCommand(initCommand);
        }

        private static void ReplSetReconfig(
            AdminHelper adminHelper,
            bool force)
        {
            var currentInstance = RoleEnvironment.CurrentRoleInstance;

            var instances = RoleEnvironment.Roles[
                currentInstance.Role.Name].Instances;

            foreach (var instance in instances)
            {
                var replicaEndPoint = ConnectionUtilities.GetReplicaEndPoint(instance);

                if (instance != currentInstance)
                {
                    DatabaseHelper.EnsureMongodIsListening(replicaEndPoint);
                }
            }

            var version = 1;

            for (var versionRetryCount = 0; versionRetryCount < 2; )
            {
                try
                {
                    var reconfigCommand = new CommandDocument
                    {
                        { "replSetReconfig", BuildReplSetConfigDocument(version) },
                    };

                    if (force)
                    {
                        reconfigCommand.Add("force", 1);
                    }

                    adminHelper.RunAdminCommand(reconfigCommand);
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

        private static BsonDocument BuildReplSetConfigDocument(int version)
        {
            var membersDocument = new BsonArray();
            foreach (var instance in DatabaseHelper.GetMongoDBRoleInstances())
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

        private static int GetPrimaryReplicaId(
            ReplSetStatus replSetStatus,
            bool allowSecondary)
        {
            var secondaryCount = 0;

            var lowestSecondaryId = -1;

            foreach (var member in replSetStatus.Members)
            {
                if (member.State == ReplSetMemberState.Primary)
                {
                    return member.Id;
                }

                if (member.State == ReplSetMemberState.Secondary)
                {
                    if (lowestSecondaryId == -1 ||
                        lowestSecondaryId > member.Id)
                    {
                        lowestSecondaryId = member.Id;
                    }

                    ++secondaryCount;
                }
                else if (member.State != ReplSetMemberState.Down)
                {
                    // cluster state undefined
                    allowSecondary = false;
                }
            }

            // when no primary can be declared use the secondary with the
            // lowest replica id
            if (secondaryCount >= 1 &&
                secondaryCount <= (replSetStatus.Members.Count / 2 + 1) &&
                allowSecondary)
            {
                return lowestSecondaryId;
            }

            return -1;
        }

        // returns true if the current replica has the lowest replica id
        private bool HasLowestReplicaId()
        {
            foreach (var instance in DatabaseHelper.GetMongoDBRoleInstances())
            {
                var replicaId = ConnectionUtilities.GetReplicaId(instance);

                if (this.currentId > replicaId)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
