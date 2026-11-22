using Miaosoft.Sim.Samples.NetworkSession;
using Miaosoft.Sim.Server.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
if (!builder.Configuration.GetValue<bool>("Demo:Enabled"))
    throw new InvalidOperationException("Select an admission policy explicitly. For the local sample pass --Demo:Enabled=true.");
string address = builder.Configuration["urls"] ?? "http://127.0.0.1:5050";
if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? endpoint) || !endpoint.IsLoopback ||
    endpoint.Scheme is not ("http" or "https"))
    throw new InvalidOperationException("The anonymous demonstration must bind one loopback HTTP(S) endpoint.");
builder.WebHost.UseUrls(address);
await using var session = await NeutralArena.CreateAsync(Path.Combine(AppContext.BaseDirectory, "combat.yaml"), enableDemoAdmission: true);
builder.Services.AddMiaosoftSession(session);
await using WebApplication app = builder.Build();
app.MapMiaosoftSession();
await app.RunAsync();
