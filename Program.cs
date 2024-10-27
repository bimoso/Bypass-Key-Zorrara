using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using System.Net.Security;
using System.Linq;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;

public class ApiEndpoint
{
    public required string UrlPattern { get; set; }
    public required Func<dynamic> ResponseGenerator { get; set; }
}

public class ProxyManager : IDisposable
{
    private readonly ProxyServer proxyServer;
    private readonly Dictionary<string, ApiEndpoint> endpoints;
    private readonly int proxyPort;
    private bool proxyEnabled = false;

    [DllImport("wininet.dll")]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    public ProxyManager(int port = 8060)
    {
        proxyServer = new ProxyServer(false);
        endpoints = new Dictionary<string, ApiEndpoint>();
        proxyPort = port;
    }

    public void AddEndpoint(string name, ApiEndpoint endpoint)
    {
        endpoints[name] = endpoint;
    }

    [SupportedOSPlatform("windows")]
    private void SetWindowsProxy(bool enable)
    {
        try
        {
            const string userRoot = "HKEY_CURRENT_USER";
            const string subKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
            const string keyName = userRoot + "\\" + subKey;

            Registry.SetValue(keyName, "ProxyEnable", enable ? 1 : 0);
            Registry.SetValue(keyName, "ProxyServer", $"127.0.0.1:{proxyPort}");

            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);

            proxyEnabled = enable;
        }
        catch (Exception ex)
        {
            LogError("Error configurando el proxy de Windows", ex);
        }
    }

    public async Task StartProxy()
    {
        try
        {
            ConfigureSSL();
            ConfigureProxyServer();
            await StartProxyServer();
            SetWindowsProxy(true);
            PrintStatus();
            await WaitForEscKey();
        }
        catch (Exception ex)
        {
            LogError("Error al iniciar el proxy", ex);
            throw;
        }
    }

    private void ConfigureSSL()
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
        proxyServer.CertificateManager.CreateRootCertificate(false);
        proxyServer.CertificateManager.TrustRootCertificate(true);
        proxyServer.ForwardToUpstreamGateway = true;
    }

    private void ConfigureProxyServer()
    {
        var explicitEndpoint = new ExplicitProxyEndPoint(IPAddress.Any, proxyPort, true);

        proxyServer.AddEndPoint(explicitEndpoint);

        proxyServer.BeforeRequest += OnRequestAsync;
        proxyServer.BeforeResponse += OnResponseAsync;
        proxyServer.ServerCertificateValidationCallback += OnCertificateValidation;
        proxyServer.ClientCertificateSelectionCallback += OnCertificateSelection;

        proxyServer.Start();
    }

    private async Task OnRequestAsync(object sender, SessionEventArgs e)
    {

        await ProcessRequestAsync(sender, e);
    }

    private async Task OnResponseAsync(object sender, SessionEventArgs e)
    {
        await ProcessResponseAsync(sender, e);
    }

    private async Task ProcessRequestAsync(object sender, SessionEventArgs e)
    {
        var request = e.HttpClient.Request;
        string url = request.RequestUri.ToString();

        var matchingEndpoint = endpoints.Values.FirstOrDefault(ep => MatchesEndpoint(url, ep));


        if (matchingEndpoint != null)
        {
            try
            {
                LogRequest(request);
                string requestBody = await e.GetRequestBodyAsString();


                var modifiedResponse = matchingEndpoint.ResponseGenerator();
                string modifiedBody = JsonSerializer.Serialize(modifiedResponse);

                var headers = new Dictionary<string, HttpHeader>
                {
                    { "Content-Type", new HttpHeader("Content-Type", "application/json; charset=utf-8") }
                };
                e.Ok(modifiedBody, headers);

                LogSuccess("Respuesta modificada exitosamente");
            }
            catch (Exception ex)
            {
                LogError("Error al procesar la solicitud", ex);
                throw;
            }
        }
    }

    private async Task ProcessResponseAsync(object sender, SessionEventArgs e)
    {
        try
        {
            var endpoint = endpoints.Values.FirstOrDefault(ep => MatchesEndpoint(e.HttpClient.Request.RequestUri.ToString(), ep));

            if (endpoint != null)
            {
                // Generar la nueva respuesta modificada
                var modifiedResponse = endpoint.ResponseGenerator();

                // Serializar la respuesta a JSON
                string jsonResponse = JsonSerializer.Serialize(modifiedResponse);

                // Modificar la respuesta actual
                e.HttpClient.Response.StatusCode = (int)HttpStatusCode.OK;
                e.HttpClient.Response.Headers.RemoveHeader("Content-Length");
                e.HttpClient.Response.Headers.AddHeader("Content-Type", "application/json");
                e.SetResponseBodyString(jsonResponse);

                LogSuccess("Respuesta modificada exitosamente");
            }
        }
        catch (Exception ex)
        {
            LogError("Error al modificar la respuesta", ex);
        }
    }


    private Task OnCertificateSelection(object sender, CertificateSelectionEventArgs e)
    {
        e.ClientCertificate = proxyServer.CertificateManager.RootCertificate;
        return Task.CompletedTask;
    }

    private Task OnCertificateValidation(object sender, CertificateValidationEventArgs e)
    {
        e.IsValid = true;
        return Task.CompletedTask;
    }

    private async Task StartProxyServer()
    {
        if (!proxyServer.ProxyRunning)
        {
            proxyServer.Start();
            foreach (var endpoint in proxyServer.ProxyEndPoints)
            {

            }
            await Task.CompletedTask;
        }
        else
        {

        }
    }

    private void PrintStatus()
    {
        Console.WriteLine("Esperando solicitudes...");
        Console.WriteLine("\nPresiona ESC para salir");
    }

    private async Task WaitForEscKey()
    {
        while (true)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true).Key;
                if (key == ConsoleKey.Escape)
                {

                    break;
                }
            }
            await Task.Delay(100);
        }
    }

    private bool MatchesEndpoint(string url, ApiEndpoint endpoint)
    {
        return Regex.IsMatch(url, endpoint.UrlPattern, RegexOptions.IgnoreCase);
    }

    private void LogRequest(Titanium.Web.Proxy.Http.Request request)
    {
        Console.WriteLine($"\nSolicitud detectada: {request.Method} {request.RequestUri}");
        Console.WriteLine("Headers de la solicitud:");
        foreach (var header in request.Headers)
        {
            Console.WriteLine($"  {header.Name}: {header.Value}");
        }
    }

    private void LogSuccess(string message, string? details = null)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n{message}:");
        Console.ResetColor();
        if (details != null)
        {
            Console.WriteLine(details);
        }
    }

    private void LogError(string message, Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n{message}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        Console.ResetColor();
    }

    public void Dispose()
    {
        try
        {
            if (proxyEnabled)
            {
                SetWindowsProxy(false);
            }
            proxyServer.BeforeRequest -= OnRequestAsync;
            proxyServer.BeforeResponse -= OnResponseAsync;
            proxyServer.Stop();
            proxyServer.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al limpiar recursos: {ex.Message}");
        }
    }
}

class Program
{
    static async Task Main(string[] args)
    {
        using (var proxyManager = new ProxyManager())
        {
            proxyManager.AddEndpoint("zorara", new ApiEndpoint
            {
                UrlPattern = @"getzorara\.online(:\d+)?/check_key",
                ResponseGenerator = () => new { message = "Key is valid." }
            });

            // Endpoint para redeem_key
            proxyManager.AddEndpoint("zorara_redeem", new ApiEndpoint
            {
                UrlPattern = @"getzorara\.online(:\d+)?/redeem_key",
                ResponseGenerator = () => new { message = "Key redeemed successfully." }
            });

            await proxyManager.StartProxy();
        }
    }
}
