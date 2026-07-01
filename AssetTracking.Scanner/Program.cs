using AssetTracking.Scanner;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<BleScannerService>();

var host = builder.Build();
host.Run();
