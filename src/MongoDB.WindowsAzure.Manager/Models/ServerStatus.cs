/*
 * Copyright 2010-2012 10gen Inc.
 * file : ServerStatus.cs
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
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Linq;
    using System.Web;
    using MongoDB.Bson;
    using System.Globalization;
    using MongoDB.Bson.Serialization.Attributes;
    using MongoDB.WindowsAzure.Common.Bson;

    /// <summary>
    /// Represents the status of one MongoDB server.
    /// </summary>
    public class ServerStatus
    {
        /// <summary>
        /// Returns all the servers that we're aware of in the current replica set.
        /// </summary>
        public static List<ReplSetMember> List
        {
            get
            {
                var replSetStatus = ReplicaSetStatus.GetStatus().Value;

                if (replSetStatus == null)
                {
                    return new List<ReplSetMember>();
                }
                
                return replSetStatus.Members;
            }
        }

        /// <summary>
        /// Returns the server that is currently the primary.
        /// </summary>
        public static ReplSetMember Primary
        {
            get
            {
                return List.Find(s => s.State == ReplSetMemberState.Primary);
            }
        }

        /// <summary>
        /// Returns the server with the given ID.
        /// </summary>
        public static ReplSetMember Get(int id)
        {
            return List.Find(s => s.Id == id);
        }
    }
}