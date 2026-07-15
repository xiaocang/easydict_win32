using System;
using Easydict.WindowsAI;
using Easydict.WindowsAI.Services;

var client = PhiSilicaAvailability.Client;

Console.WriteLine("=== Phi Silica Probe ===");

try
{
    var state = client.GetReadyState();
    Console.WriteLine($"GetReadyState   : {state}");
}
catch (Exception ex)
{
    Console.WriteLine($"GetReadyState   : THREW {ex.GetType().Name}: {ex.Message}");
}

try
{
    var fp = client.GetHealthFingerprint();
    Console.WriteLine($"HealthFingerprint:");
    Console.WriteLine($"  OsBuild               : {fp.OsBuild}");
    Console.WriteLine($"  Ubr                   : {(fp.Ubr.HasValue ? fp.Ubr.ToString() : "<unknown>")}");
    Console.WriteLine($"  WindowsAppSdkVersion  : {fp.WindowsAppSdkVersion}");
    Console.WriteLine($"  ProcessArchitecture   : {fp.ProcessArchitecture}");
    Console.WriteLine($"  BackendName           : {fp.BackendName}");
    Console.WriteLine($"  ComponentMarker       : {fp.ComponentMarker}");
    Console.WriteLine($"  WindowsActivated      : {(fp.WindowsActivated.HasValue ? fp.WindowsActivated.ToString() : "<unknown>")}");
    Console.WriteLine($"  PhiSilicaAiComponents : {(fp.PhiSilicaAiComponentsPresent.HasValue ? fp.PhiSilicaAiComponentsPresent.ToString() : "<unknown>")}");
}
catch (Exception ex)
{
    Console.WriteLine($"HealthFingerprint: THREW {ex.GetType().Name}: {ex.Message}");
}
