using System;
using System.Collections.Generic;
using ParametersManagement.LibApiClientParameters;

namespace UsbCopyConsole.Models;

public sealed class UsbCopyConsoleParameters : IParametersWithApiClients
{
    public string? LogFolder { get; set; }
    public Dictionary<string, UsbCopyConsoleProjectModel> Projects { get; init; } = new();

    public Dictionary<string, ApiClientSettings> ApiClients { get; init; } = new();

    public bool CheckBeforeSave()
    {
        return true;
    }

    public UsbCopyConsoleProjectModel? GetProject(string projectName)
    {
        return Projects.GetValueOrDefault(projectName);
    }

    public UsbCopyConsoleProjectModel GetProjectRequired(string projectName)
    {
        return GetProject(projectName) ??
               throw new InvalidOperationException($"project with name {projectName} does not found");
    }
}
