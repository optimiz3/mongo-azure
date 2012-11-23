/*
 * Copyright 2010-2012 10gen Inc.
 * file : ConnectionUtilities.cs
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

    using System;
    using System.Configuration;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.Net;

    /// <summary>
    /// Provides utility methods to easily connect to the MongoDB servers in your deployment.
    /// </summary>
    public static class ConnectionUtilities
    {
        /// <summary>
        /// Creates a MongoServer connection to the MongoDB installation in the curent deployment.
        /// Use this to connect to MongoDB in your application.
        /// Cache this connection and re-obtain t only if there is a connection exception.
        /// </summary>
        /// <param name="slaveOk">true to be able to route reads to secondaries, false otherwise.</param>
        /// <returns>A MongoServer connection that has SafeMode set to true.</returns>
        public static MongoServer CreateServer(
            bool slaveOk)
        {
            var instances = GetMongoDBInstances();

            return CreateServer(
                slaveOk,
                RoleSettings.ReplicaSetName,
                GetServerAddresses(instances));
        }

        /// <summary>
        /// Creates a MongoServer connection to a MongoDB installation.
        /// Use this to connect to MongoDB in your application.
        /// Cache this connection and re-obtain t only if there is a connection exception.
        /// </summary>
        /// <param name="slaveOk">true to be able to route reads to secondaries, false otherwise.</param>
        /// <param name="replicaSetName">The name of the replica set to connect to, null for a direct connection.</param>
        /// <param name="endPoint">The IPEndPoint of a MongoDB installation.</param>
        /// <returns>A MongoServer connection that has SafeMode set to true.</returns>
        public static MongoServer CreateServer(
            bool slaveOk,
            string replicaSetName,
            IPEndPoint endPoint)
        {
            return CreateServer(
                slaveOk,
                replicaSetName,
                new[] { GetServerAddress(endPoint) });
        }

        /// <summary>
        /// Creates a MongoServer connection to a MongoDB installation.
        /// Use this to connect to MongoDB in your application.
        /// Cache this connection and re-obtain t only if there is a connection exception.
        /// </summary>
        /// <param name="slaveOk">true to be able to route reads to secondaries, false otherwise.</param>
        /// <param name="replicaSetName">The name of the replica set to connect to, null for a direct connection.</param>
        /// <param name="instance">The RoleInstance of a MongoDB installation.</param>
        /// <returns>A MongoServer connection that has SafeMode set to true.</returns>
        public static MongoServer CreateServer(
            bool slaveOk,
            string replicaSetName,
            RoleInstance instance)
        {
            return CreateServer(
                slaveOk,
                replicaSetName,
                new[] { GetServerAddress(instance) });
        }

        /// <summary>
        /// Creates a MongoServer connection to a MongoDB installation.
        /// Use this to connect to MongoDB in your application.
        /// Cache this connection and re-obtain t only if there is a connection exception.
        /// </summary>
        /// <param name="slaveOk">true to be able to route reads to secondaries, false otherwise.</param>
        /// <param name="replicaSetName">The name of the replica set to connect to, null for a direct connection.</param>
        /// <param name="servers">The MongoServerAddresses of a MongoDB installation.</param>
        /// <returns>A MongoServer connection that has SafeMode set to true.</returns>
        public static MongoServer CreateServer(
            bool slaveOk,
            string replicaSetName,
            IEnumerable<MongoServerAddress> servers)
        {
            var serverSettings = new MongoServerSettings
            {
                Servers = servers,
                SlaveOk = slaveOk,
                SafeMode = SafeMode.True
            };

            if (RoleSettings.Authenticate && RoleSettings.AdminCredentials != null)
            {
                serverSettings.CredentialsStore.AddCredentials("admin", RoleSettings.AdminCredentials);
            }

            if (replicaSetName != null)
            {
                serverSettings.ReplicaSetName = replicaSetName;
                serverSettings.ConnectionMode = ConnectionMode.ReplicaSet;
            }
            else
            {
                serverSettings.ConnectionMode = ConnectionMode.Direct;
            }

            return MongoServer.Create(serverSettings);
        }

        /// <summary>
        /// Gets the Azure cloud drive container name for a MongoDB replica set.
        /// </summary>
        /// <param name="replicaSetName">The replica set name.</param>
        /// <returns>The cloud drive container name for a MongoDB replica set.</returns>
        public static string GetDataContainerName(string replicaSetName)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                Constants.MongoDataContainerName,
                replicaSetName);
        }

        /// <summary>
        /// Gets the Azure cloud drive blob name for a MongoDB replica id.
        /// </summary>
        /// <param name="replicaId">The replica id of a MongoDB replica id.</param>
        /// <returns>The cloud drive blob name for a MongoDB replica id.</returns>
        public static string GetDataBlobName(int replicaId)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                Constants.MongoDataBlobName,
                replicaId);
        }

        /// <summary>
        /// Gets the IPEndPoints in most secure to least secure order for a MongoDB replica instance.
        /// </summary>
        /// <param name="instance">The MongoDB replica instance.</param>
        /// <returns>An array of IPEndPoints for the MongoDB replica instance.</returns>
        /// <remarks>Preserves endpoint order with internal endpoints being returned first.</remarks>
        public static IPEndPoint[] GetReplicaEndPoints(RoleInstance instance)
        {
            var replicaId = GetReplicaId(instance);

            var replicaEndPoints = new IPEndPoint[instance.InstanceEndpoints.Count];

            var index = 0;

            var port = -1;

            // select internal-only endpoints first
            if (index < replicaEndPoints.Length)
            {
                foreach (var instanceEndpoint in instance.InstanceEndpoints.Values)
                {
                    if (instanceEndpoint.IPEndpoint != null &&
                        instanceEndpoint.PublicIPEndpoint == null)
                    {
                        var endPoint = instanceEndpoint.IPEndpoint;

                        if (port == -1)
                        {
                            port = endPoint.Port;
                        }
                        else if (port != endPoint.Port)
                        {
                            throw new ConfigurationErrorsException(
                                "Multiple ports are not supported");
                        }

                        // Workaround for
                        if (RoleEnvironment.IsEmulated)
                        {
                            endPoint.Port += replicaId;
                        }

                        replicaEndPoints[index] = endPoint;

                        ++index;
                    }
                }
            }

            // select other internal-accessible endpoints
            if (index < replicaEndPoints.Length)
            {
                foreach (var instanceEndpoint in instance.InstanceEndpoints.Values)
                {
                    if (instanceEndpoint.IPEndpoint != null &&
                        instanceEndpoint.PublicIPEndpoint != null)
                    {
                        var endPoint = instanceEndpoint.IPEndpoint;

                        if (port == -1)
                        {
                            port = endPoint.Port;
                        }
                        else if (port != endPoint.Port)
                        {
                            throw new ConfigurationErrorsException(
                                "Multiple ports are not supported");
                        }

                        // Workaround for
                        if (RoleEnvironment.IsEmulated)
                        {
                            endPoint.Port += replicaId;
                        }

                        replicaEndPoints[index] = endPoint;

                        ++index;
                    }
                }
            }

            if (index < replicaEndPoints.Length)
            {
                Array.Resize(ref replicaEndPoints, index);
            }

            return replicaEndPoints;
        }

        /// <summary>
        /// Gets the first IPEndPoint in most secure to least secure order for a MongoDB replica instance.
        /// </summary>
        /// <param name="instance">The MongoDB replica instance.</param>
        /// <returns>An IPEndPoint for the MongoDB replica instance.</returns>
        /// <remarks>Preserves endpoint order with internal endpoints being returned first.</remarks>
        public static IPEndPoint GetReplicaEndPoint(RoleInstance instance)
        {
            var replicaEndPoints = GetReplicaEndPoints(instance);

            if (replicaEndPoints.Length == 0)
            {
                var message = string.Format(
                    "Instance \"{0}\" has no endpoints",
                    instance.Id);

                throw new ArgumentException(message, "instance");
            }

            return replicaEndPoints[0];
        }

        /// <summary>
        /// Returns the set of all worker roles in the current deployment that are hosting MongoDB.
        /// Throws a ConfigurationErrorsException if they could not be retrieved.
        /// </summary>
        public static ReadOnlyCollection<RoleInstance> GetMongoDBInstances()
        {
            Role mongoWorkerRole;

            if (!RoleEnvironment.Roles.TryGetValue(
                Constants.MongoDBWorkerRoleName,
                out mongoWorkerRole))
            {
                var message = string.Format(
                    CultureInfo.InvariantCulture,
                    "Unable to find MongoDB worker role '{0}'",
                    Constants.MongoDBWorkerRoleName);

                throw new ConfigurationErrorsException(message);
            }

            return mongoWorkerRole.Instances;
        }

        /// <summary>
        /// Extracts the instance number from the instance's ID string.
        /// </summary>
        /// <param name="id">The instance's string ID (eg, deployment17(48).MongoDBReplicaSet.MongoDB.WindowsAzure.MongoDBRole_IN_2)</param>
        /// <returns>The instance number for the instance.</returns>
        public static int GetReplicaId(RoleInstance instance)
        {
            if (instance == null)
            {
                throw new ArgumentNullException("instance");
            }

            var id = instance.Id;

            var index = id.LastIndexOf("_", StringComparison.Ordinal);

            if (index < 0)
            {
                var message = string.Format(
                    CultureInfo.InvariantCulture,
                    "Instance id \"{0}\" does not contain a '_' character",
                    id);

                throw new FormatException(message);
            }

            return int.Parse(
                id.Substring(index + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
        }

        private static IEnumerable<MongoServerAddress> GetServerAddresses(
            IEnumerable<RoleInstance> instances)
        {
            foreach (var instance in instances)
            {
                yield return GetServerAddress(instance);
            }
        }

        private static MongoServerAddress GetServerAddress(RoleInstance instance)
        {
            if (instance == null)
            {
                throw new ArgumentNullException("instance");
            }

            var replicaEndPoint = GetReplicaEndPoint(instance);

            return GetServerAddress(replicaEndPoint);
        }

        private static MongoServerAddress GetServerAddress(IPEndPoint endPoint)
        {
            if (endPoint == null)
            {
                throw new ArgumentNullException("endPoint");
            }

            return new MongoServerAddress(
                endPoint.Address.ToString(),
                endPoint.Port);
        }
    }
}
