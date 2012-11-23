/*
 * Copyright 2010-2012 10gen Inc.
 * file : ReplicaSetStatus.cs
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

namespace MongoDB.WindowsAzure.Manager.Models
{
    using Microsoft.WindowsAzure.ServiceRuntime;

    using MongoDB.Bson.Serialization;
    using MongoDB.Driver;
    using MongoDB.WindowsAzure.Common;
    using MongoDB.WindowsAzure.Common.Bson;

    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Stores the status of the replica set.
    /// </summary>
    public class ReplicaSetStatus
    {
        public enum State
        {
            Initializing,
            OK,
            Error
        }

        /// <summary>
        /// The state of the replica set.
        /// </summary>
        public State Status
        {
            get
            {
                if (this.Error != null)
                {
                    return State.Error;
                }

                if (this.Value == null || this.Value.StartupStatus.HasValue)
                {
                    return State.Initializing;
                }

                return State.OK;
            }
        }

        public ReplSetStatus Value
        {
            get;
            set;
        }

        /// <summary>
        /// The error we received while fetching the status, if Status is Error.
        /// </summary>
        public MongoException Error { get; private set; }

        /// <summary>
        /// Fetches the current status.
        /// </summary>
        public static ReplicaSetStatus GetStatus()
        {
            if (!RoleEnvironment.IsAvailable)
            {
                return GetDummyStatus();
            }

            var server = ConnectionUtilities.CreateServer(true);
            if (server.State == MongoServerState.Disconnected)
            {
                try
                {
                    server.Connect();
                }
                catch (MongoConnectionException e)
                {
                    return new ReplicaSetStatus { Error = e };
                }
            }

            ReplicaSetStatus status;

            if (RoleSettings.ReplicaSetName == null)
            {
                var replSetStatus = new ReplSetStatus
                {
                    Members = new List<ReplSetMember>()
                    {
                        new ReplSetMember
                        {
                            Id = 0,
                            Name = server.Instance.Address.ToString(),
                            Health = ReplSetMemberHealth.Up,
                            State = ReplSetMemberState.Primary,
                        },
                    },
                };

                status = new ReplicaSetStatus
                {
                    Value = replSetStatus,
                };

                return status;
            }

            try
            {
                status = new ReplicaSetStatus
                {
                    Value = BsonSerializer.Deserialize<ReplSetStatus>(
                        server["admin"].RunCommand("replSetGetStatus").Response),
                };
            }
            catch (MongoException e)
            {
                return new ReplicaSetStatus { Error = e };
            }

            return status;
        }

        /// <summary>
        /// Returns dummy server information for when the ASP.NET app is being run directly (without Azure).
        /// </summary>
        /// <returns></returns>
        public static ReplicaSetStatus GetDummyStatus()
        {
            return new ReplicaSetStatus
            {
                Value = new ReplSetStatus
                {
                    ReplicaSetName = "rs-offline-dummy-data",
                    Members = new List<ReplSetMember>
                    {
                        new ReplSetMember
                        {
                            Id = 0,
                            Name = "localhost:27018",
                            Health = ReplSetMemberHealth.Up,
                            State = ReplSetMemberState.Secondary,
                            LastHeartBeat = DateTime.Now.Subtract(new TimeSpan(0, 0, 1)),
                            LastOperationTime = DateTime.Now,
                            PingTime = new Random().Next(20, 600)
                        },
                        new ReplSetMember
                        {
                            Id = 1,
                            Name = "localhost:27019",
                            Health = ReplSetMemberHealth.Up,
                            State = ReplSetMemberState.Primary,
                            LastHeartBeat = DateTime.MinValue,
                            LastOperationTime = DateTime.Now,
                            PingTime = 0
                        },
                        new ReplSetMember
                        {
                            Id = 2,
                            Name = "localhost:27020",
                            Health = ReplSetMemberHealth.Down,
                            State = ReplSetMemberState.Down,
                            LastHeartBeat = DateTime.MinValue,
                            LastOperationTime = DateTime.MinValue,
                            PingTime = 0,
                        },
                    },
                }
            };
        }
    }
}