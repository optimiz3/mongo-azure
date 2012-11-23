/*
 * Copyright 2010-2012 10gen Inc.
 * file : ReplSetStatus.cs
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
    using System.Collections.Generic;

    /// <summary>
    /// Stores the status of the replica set.
    /// </summary>
    public class ReplSetStatus
    {
        /// <summary>
        /// The startup status of the replica set.
        /// </summary>
        [BsonElement("startupStatus")]
        [BsonIgnoreIfDefault]
        public ReplSetStartupStatus? StartupStatus { get; set; }

        /// <summary>
        /// The name of the replica set, if Status is OK.
        /// </summary>
        [BsonElement("set")]
        public string ReplicaSetName { get; set; }

        [BsonElement("date")]
        public DateTime CurrentDateTime { get; set; }

        [BsonElement("myState")]
        public ReplSetMemberState MyState { get; set; }

        /// <summary>
        /// The actual servers in the replica set.
        /// </summary>
        [BsonElement("members")]
        public List<ReplSetMember> Members { get; set; }

        [BsonElement("syncingTo")]
        public string SyncingTo { get; set; }

        [BsonElement("ok")]
        public bool OK { get; set; }
    }
}