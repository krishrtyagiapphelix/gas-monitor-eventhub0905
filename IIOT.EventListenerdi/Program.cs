using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        // Register IoTDataProcessor so Azure Functions can detect it
        services.AddLogging();
        services.AddSingleton<IoTDataProcessor>();
    })
    .Build();

host.Run();