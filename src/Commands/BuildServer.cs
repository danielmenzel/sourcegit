using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class BuildServer : Command
    {
        public BuildServer(string repo, bool isShared)
        {
            WorkingDirectory = repo;
            Context = repo;

            if (isShared)
            {
                var storage = $"{repo}/.buildserver";
                _isStorageFileExists = File.Exists(storage);
                _baseArg = $"config -f {storage.Quoted()}";
            }
            else
            {
                _isStorageFileExists = true;
                _baseArg = "config --local";
            }
        }

        public async Task ReadAllAsync(List<Models.BuildServerIntegration> outs, bool isShared)
        {
            if (!_isStorageFileExists)
                return;

            Args = $"{_baseArg} -l";

            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (rs.IsSuccess)
            {
                var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length < 2)
                        continue;

                    var key = parts[0];
                    var value = parts[1];

                    if (!key.StartsWith("buildserver.", StringComparison.Ordinal))
                        continue;

                    if (key.EndsWith(".type", StringComparison.Ordinal))
                    {
                        var prefixLen = "buildserver.".Length;
                        var suffixLen = ".type".Length;
                        var name = key.Substring(prefixLen, key.Length - prefixLen - suffixLen);
                        FindOrAdd(outs, name, isShared).Type = value;
                    }
                    else if (key.EndsWith(".serverurl", StringComparison.Ordinal))
                    {
                        var prefixLen = "buildserver.".Length;
                        var suffixLen = ".serverurl".Length;
                        var name = key.Substring(prefixLen, key.Length - prefixLen - suffixLen);
                        FindOrAdd(outs, name, isShared).ServerUrl = value;
                    }
                    else if (key.EndsWith(".projectname", StringComparison.Ordinal))
                    {
                        var prefixLen = "buildserver.".Length;
                        var suffixLen = ".projectname".Length;
                        var name = key.Substring(prefixLen, key.Length - prefixLen - suffixLen);
                        FindOrAdd(outs, name, isShared).ProjectName = value;
                    }
                    else if (key.EndsWith(".enable", StringComparison.Ordinal))
                    {
                        var prefixLen = "buildserver.".Length;
                        var suffixLen = ".enable".Length;
                        var name = key.Substring(prefixLen, key.Length - prefixLen - suffixLen);
                        var integration = FindOrAdd(outs, name, isShared);
                        integration.EnableQueryBuildStatus = value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("1", StringComparison.Ordinal);
                    }
                }
            }
        }

        public async Task<bool> AddAsync(Models.BuildServerIntegration integration, string name)
        {
            Args = $"{_baseArg} buildserver.{name.Quoted()}.type {integration.Type.Quoted()}";
            var succ = await ExecAsync().ConfigureAwait(false);
            if (!succ)
                return false;

            Args = $"{_baseArg} buildserver.{name.Quoted()}.serverurl {integration.ServerUrl.Quoted()}";
            succ = await ExecAsync().ConfigureAwait(false);
            if (!succ)
                return false;

            Args = $"{_baseArg} buildserver.{name.Quoted()}.projectname {integration.ProjectName.Quoted()}";
            succ = await ExecAsync().ConfigureAwait(false);
            if (!succ)
                return false;

            Args = $"{_baseArg} buildserver.{name.Quoted()}.enable {(integration.EnableQueryBuildStatus ? "true" : "false")}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UpdateTypeAsync(Models.BuildServerIntegration integration, string name)
        {
            Args = $"{_baseArg} buildserver.{name.Quoted()}.type {integration.Type.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UpdateServerUrlAsync(Models.BuildServerIntegration integration, string name)
        {
            Args = $"{_baseArg} buildserver.{name.Quoted()}.serverurl {integration.ServerUrl.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UpdateProjectNameAsync(Models.BuildServerIntegration integration, string name)
        {
            Args = $"{_baseArg} buildserver.{name.Quoted()}.projectname {integration.ProjectName.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UpdateEnableAsync(Models.BuildServerIntegration integration, string name)
        {
            Args = $"{_baseArg} buildserver.{name.Quoted()}.enable {(integration.EnableQueryBuildStatus ? "true" : "false")}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> RemoveAsync(string name)
        {
            if (!_isStorageFileExists)
                return true;

            Args = $"{_baseArg} --remove-section buildserver.{name.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }

        private Models.BuildServerIntegration FindOrAdd(List<Models.BuildServerIntegration> integrations, string name, bool isShared)
        {
            var integration = integrations.Find(x => x.Type.Equals(name, StringComparison.Ordinal));
            if (integration != null)
                return integration;

            integration = new Models.BuildServerIntegration() { IsShared = isShared, Type = name };
            integrations.Add(integration);
            return integration;
        }

        private readonly bool _isStorageFileExists;
        private readonly string _baseArg;
    }
}
