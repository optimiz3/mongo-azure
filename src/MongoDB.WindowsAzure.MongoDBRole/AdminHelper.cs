/*
 * Copyright 2010-2012 10gen Inc.
 * file : AdminHelper.cs
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
    using MongoDB.Driver;
    using MongoDB.WindowsAzure.Common;

    using System;

    internal class AdminHelper
    {
        private static readonly string AuthenticationRequired = "need to login";

        private readonly bool defaultAuthenticate;

        private MongoServer server;
        
        private MongoCredentials credentials;

        private AdminAuthState authState;

        internal AdminHelper(int port)
            : this(port, true)
        {
        }

        internal AdminHelper(int port, bool defaultAuthenticate)
        {
            this.defaultAuthenticate = defaultAuthenticate;

            // Admin provisoning is only allowed over a localhost connection
            this.server = new MongoClient(
                new MongoClientSettings
                {
                    Servers = new MongoServerAddress[]
                    {
                        new MongoServerAddress("localhost", port),
                    },
                    ReadPreference = ReadPreference.Nearest,
                    WriteConcern = WriteConcern.Acknowledged,
                }).GetServer();

            if (RoleSettings.Authenticate)
            {
                var credentials = RoleSettings.AdminCredentials;

                if (credentials != null)
                {
                    this.authState = AdminAuthState.Unknown;

                    this.credentials = credentials;
                }
            }
        }

        internal AdminAuthState AuthState
        {
            get
            {
                return this.authState;
            }
        }

        internal MongoDatabase GetAdminDatabase()
        {
            return this.GetAdminDatabase(false);
        }

        // gets a database authenticated as admin
        internal MongoDatabase GetDatabase(string databaseName)
        {
            if (this.authState != AdminAuthState.Enabled)
            {
                throw new InvalidOperationException();
            }

            return server.GetDatabase(
                databaseName,
                this.credentials,
                WriteConcern.Acknowledged);
        }

        internal void NotifyAuthEnabled()
        {
            if (this.authState != AdminAuthState.Preinit)
            {
                throw new InvalidOperationException();
            }

            this.authState = AdminAuthState.Enabled;
        }

        internal CommandResult RunAdminCommand(
            CommandDocument command)
        {
            return this.RunCommand(command, false);
        }

        internal CommandResult RunCommand(
            CommandDocument command,
            bool ignoreIfDisconnected)
        {
            if (this.server.State == MongoServerState.Disconnected)
            {
                if (ignoreIfDisconnected)
                {
                    return null;
                }

                this.server.Connect();
            }

            CommandResult commandResult;

            for (; ; )
            {
                var adminDatabase = this.GetAdminDatabase(true);

                try
                {
                    commandResult = adminDatabase.RunCommand(command);

                    if (this.authState == AdminAuthState.Unknown)
                    {
                        this.authState = this.defaultAuthenticate ?
                            AdminAuthState.Enabled : AdminAuthState.Preinit;
                    }
                }
                catch (MongoAuthenticationException)
                {
                    if (!this.HandleAuthenticationError())
                    {
                        throw;
                    }

                    continue;
                }
                catch (MongoCommandException e)
                {
                    if (e.CommandResult.ErrorMessage != AuthenticationRequired ||
                        !this.HandleAuthenticationError())
                    {
                        throw;
                    }

                    continue;
                }

                break;
            }

            return commandResult;
        }

        private MongoDatabase GetAdminDatabase(bool allowUnknown)
        {
            if (this.authState == AdminAuthState.Unknown)
            {
                if (!allowUnknown)
                {
                    throw new InvalidOperationException();
                }

                return server.GetDatabase(
                    "admin",
                    this.defaultAuthenticate ?
                        this.credentials : null,
                    WriteConcern.Acknowledged);
            }

            if (this.authState != AdminAuthState.None)
            {
                return server.GetDatabase(
                    "admin",
                    this.authState != AdminAuthState.Preinit ?
                        this.credentials : null,
                    WriteConcern.Acknowledged);
            }

            return server.GetDatabase(
                "admin",
                WriteConcern.Acknowledged);
        }

        private bool HandleAuthenticationError()
        {
            if (this.authState != AdminAuthState.Unknown &&
                this.authState != AdminAuthState.Preinit)
            {
                return false;
            }

            if (this.authState == AdminAuthState.Unknown)
            {
                this.authState = AdminAuthState.Preinit;

                return true;
            }

            // authentication is enabled
            this.authState = AdminAuthState.Enabled;

            return true;
        }
    }
}
