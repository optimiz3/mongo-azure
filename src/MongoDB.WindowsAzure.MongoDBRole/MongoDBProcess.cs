/*
 * Copyright 2010-2012 10gen Inc.
 * file : MongoDBProcess.cs
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

    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;

    internal class MongoDBProcess
    {
        private const int MaxLogLevel = 5;

        private readonly string mongodPath;

        private Process process;

        #region Command Line Fields

        private int port;

        private bool noHttpInterface;

        private string replSet;

        private string logPath;

        private bool logAppend;

        private string dbPath;

        private bool directoryPerDb;

        private bool noPreAlloc;

        private bool smallFiles;

        private int opLogSizeMB;

        private string keyFile;

        private bool auth;

        private bool ipv6;

        private IPAddress[] bindIp;

        private int logLevel;

        #endregion

        public MongoDBProcess(string mongodPath)
        {
            this.mongodPath = mongodPath;
        }

        private void CheckNotStarted()
        {
            if (this.process != null)
            {
                throw new InvalidOperationException("MongoD.exe is already started");
            }
        }

        #region Properties

        public IPEndPoint EndPoint
        {
            get
            {
                IPAddress ipAddress;

                if (this.bindIp == null)
                {
                    ipAddress = this.ipv6 ?
                        IPAddress.IPv6Loopback :
                        IPAddress.Loopback;
                }
                else
                {
                    ipAddress = this.bindIp[0];
                }

                return new IPEndPoint(ipAddress, this.port);
            }
        }

        public int Port
        {
            get
            {
                return this.port;
            }
            set
            {
                this.CheckNotStarted();

                this.port = value;
            }
        }

        public bool NoHttpInterface
        {
            get
            {
                return this.noHttpInterface;
            }
            set
            {
                this.CheckNotStarted();

                this.noHttpInterface = value;
            }
        }

        public string ReplSet
        {
            get
            {
                return this.replSet;
            }
            set
            {
                this.CheckNotStarted();

                this.replSet = value;
            }
        }

        public string LogPath
        {
            get
            {
                return this.logPath;
            }
            set
            {
                this.CheckNotStarted();

                this.logPath = value;
            }
        }

        public bool LogAppend
        {
            get
            {
                return this.logAppend;
            }
            set
            {
                this.CheckNotStarted();

                this.logAppend = value;
            }
        }

        public string DbPath
        {
            get
            {
                return this.dbPath;
            }
            set
            {
                this.CheckNotStarted();

                this.dbPath = value;
            }
        }

        public bool DirectoryPerDb
        {
            get
            {
                return this.directoryPerDb;
            }
            set
            {
                this.CheckNotStarted();

                this.directoryPerDb = value;
            }
        }

        public bool NoPreAlloc
        {
            get
            {
                return this.noPreAlloc;
            }
            set
            {
                this.CheckNotStarted();

                this.noPreAlloc = value;
            }
        }

        public bool SmallFiles
        {
            get
            {
                return this.smallFiles;
            }
            set
            {
                this.CheckNotStarted();

                this.smallFiles = value;
            }
        }

        public int OpLogSizeMB
        {
            get
            {
                return this.opLogSizeMB;
            }
            set
            {
                this.CheckNotStarted();

                this.opLogSizeMB = value;
            }
        }

        public string KeyFile
        {
            get
            {
                return this.keyFile;
            }
            set
            {
                this.CheckNotStarted();

                this.keyFile = value;
            }
        }

        public bool Auth
        {
            get
            {
                return this.auth;
            }
            set
            {
                this.CheckNotStarted();

                this.auth = value;
            }
        }

        public bool IPv6
        {
            get
            {
                return this.ipv6;
            }
            set
            {
                this.CheckNotStarted();

                this.ipv6 = value;
            }
        }

        public IPAddress[] BindIp
        {
            get
            {
                return this.bindIp;
            }
            set
            {
                if (value != null && value.Length == 0)
                {
                    throw new ArgumentException("Length must be greater than 0", "value");
                }

                this.CheckNotStarted();

                this.bindIp = value;
            }
        }

        public bool IsRunning
        {
            get
            {
                return this.process != null && !this.process.HasExited;
            }
        }

        public int LogLevel
        {
            get
            {
                return this.logLevel;
            }
            set
            {
                if (value < 0 || value > MaxLogLevel)
                {
                    throw new ArgumentOutOfRangeException("value");
                }

                if (this.process != null)
                {
                    IPAddress ipAddress;

                    if (this.bindIp == null)
                    {
                        ipAddress = this.ipv6 ?
                            IPAddress.IPv6Loopback :
                            IPAddress.Loopback;
                    }
                    else
                    {
                        ipAddress = this.bindIp[0];
                    }

                    var endPoint = new IPEndPoint(ipAddress, this.port);

                    var commandDocument = new CommandDocument
                    {
                        { "setParameter", 1 },
                        { "logLevel", value },
                    };

                    var server = DatabaseHelper.GetSlaveOkConnection(endPoint);
                    server["admin"].RunCommand(commandDocument);
                }

                this.logLevel = value;
            }
        }

        #endregion

        public void Start()
        {
            var commandLine = this.BuildCommandLine();

            DiagnosticsHelper.TraceInformation("Launching mongod as \"{0}\" {1}", mongodPath, commandLine);

            // launch mongo
            try
            {
                this.process = new Process()
                {
                    StartInfo = new ProcessStartInfo(mongodPath, commandLine)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(this.mongodPath),
                        CreateNoWindow = false,
                    },
                };

                this.process.Start();
            }
            catch (Exception e)
            {
                DiagnosticsHelper.TraceErrorException("Can't start Mongo", e);
                throw; // throwing an exception here causes the VM to recycle
            }
        }

        public void WaitForExit()
        {
            if (this.process == null)
            {
                throw new InvalidOperationException("MongoD.exe is not started");
            }

            this.process.WaitForExit();
        }

        private string BuildCommandLine()
        {
            var str = new StringBuilder();

            str.AppendFormat("--port {0}", port);

            str.AppendFormat(" --dbpath \"{0}\"", this.dbPath);

            str.Append(" -");
            str.Append('v', this.logLevel + 1);

            if (this.logPath != null)
            {
                str.AppendFormat(" --logpath \"{0}\"", this.logPath);
            }

            if (this.logAppend)
            {
                str.Append(" --logappend");
            }

            if (this.noHttpInterface)
            {
                str.Append(" --nohttpinterface");
            }

            if (this.replSet != null)
            {
                str.AppendFormat(" --replSet {0}", this.replSet);
            }

            if (this.smallFiles)
            {
                str.Append(" --smallfiles");
            }

            if (this.noPreAlloc)
            {
                str.Append(" --noprealloc");
            }

            if (this.opLogSizeMB != 0)
            {
                str.AppendFormat(" --oplogSize {0}", this.opLogSizeMB);
            }

            if (this.directoryPerDb)
            {
                str.Append(" --directoryperdb");
            }

            if (this.auth)
            {
                str.Append(" --auth");
            }

            if (this.keyFile != null)
            {
                str.AppendFormat(" --keyFile \"{0}\"", this.keyFile);
            }

            if (this.ipv6)
            {
                str.Append(" --ipv6");
            }

            if (this.bindIp != null)
            {
                str.AppendFormat(
                    " --bind_ip {0}",
                    string.Join(",", this.bindIp.Select(ipAddress => ipAddress.ToString())));
            }

            return str.ToString();
        }
    }
}
