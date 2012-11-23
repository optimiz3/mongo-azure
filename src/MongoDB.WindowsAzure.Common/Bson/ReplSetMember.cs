/*
 * Copyright 2010-2012 10gen Inc.
 * file : ReplSetMember.cs
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

namespace MongoDB.WindowsAzure.Common.Bson
{
    using MongoDB.Bson;
    using MongoDB.Bson.Serialization.Attributes;

    using System;
    using System.ComponentModel;

    /// <summary>
    /// Represents the status of one MongoDB server.
    /// </summary>
    public class ReplSetMember : ISupportInitialize
    {
        /// <summary>
        /// The node's ID in the replica set.
        /// </summary>
        [BsonId]
        public int Id { get; set; }

        /// <summary>
        /// The node's name (often its address, e.g "localhost:27017").
        /// </summary>
        [BsonElement("name")]
        public string Name { get; set; }

        /// <summary>
        /// Is the node up or down?
        /// </summary>
        [BsonElement("health")]
        public ReplSetMemberHealth Health { get; set; }

        /// <summary>
        /// The current state of the node.
        /// </summary>
        [BsonElement("state")]
        public ReplSetMemberState State { get; set; }

        [BsonElement("stateStr")]
        public string StateStr { get; set; }

        [BsonElement("uptime")]
        public int Uptime { get; set; }

        /// <summary>
        /// The last time a heartbeat from this node was received by the primary.
        /// </summary>
        [BsonElement("lastHeartbeat")]
        public DateTime LastHeartBeat { get; set; }

        [BsonElement("optime")]
        public BsonValue Optime { get; set; }

        /// <summary>
        /// The last time a database operation ran on this node.
        /// </summary>
        [BsonElement("optimeDate")]
        public DateTime LastOperationTime { get; set; }

        /// <summary>
        /// The round-trip ping time to the primary, in MS.
        /// </summary>
        [BsonElement("pingMs")]
        public int PingTime { get; set; }

        [BsonElement("authenticated")]
        [BsonIgnoreIfDefault]
        public bool Authenticated { get; set; }

        [BsonElement("errmsg")]
        public string ErrorMessage { get; set; }

        [BsonElement("self")]
        [BsonIgnoreIfDefault]
        public bool Self { get; set; }

        #region ISupportInitialize Members

        void ISupportInitialize.BeginInit()
        {
        }

        /// <remarks>
        /// Corrects any conflicting or redundant data in the server's status.
        /// </remarks>
        void ISupportInitialize.EndInit()
        {
            // mongod returns the Unix epoch for down instances -- convert these to DateTime.MinValue, the .NET epoch.
            if (this.LastHeartBeat == BsonConstants.UnixEpoch)
            {
                this.LastHeartBeat = DateTime.MinValue;
            }

            if (this.LastOperationTime == BsonConstants.UnixEpoch)
            {
                this.LastOperationTime = DateTime.MinValue;
            }
        }

        #endregion
    }
}