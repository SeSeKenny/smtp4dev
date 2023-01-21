using System;
using System.IO;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Rnwood.Smtp4dev.DbModel;
using Rnwood.Smtp4dev.Hubs;
using Rnwood.Smtp4dev.Server;
using Microsoft.Extensions.Hosting;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Rewrite;
using Rnwood.Smtp4dev.Data;
using Rnwood.Smtp4dev.Service;
using Serilog;
using System.Reflection;

namespace Rnwood.Smtp4dev
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<ServerOptions>(Configuration.GetSection("ServerOptions"));
            services.Configure<RelayOptions>(Configuration.GetSection("RelayOptions"));
            services.Configure<ClientOptions>(Configuration.GetSection("ClientOptions"));

            ServerOptions serverOptions = Configuration.GetSection("ServerOptions").Get<ServerOptions>();

            services.AddDbContext<Smtp4devDbContext>(opt =>
            {
                if (string.IsNullOrEmpty(serverOptions.Database))
                {
                    Log.Logger.Information("Using in memory database.");
                    opt.UseInMemoryDatabase("main");
                }
                else
                {
                    if (serverOptions.RecreateDb && File.Exists(serverOptions.Database))
                    {
                        Log.Logger.Information("Deleting Sqlite database.");
                        File.Delete(serverOptions.Database);
                    }

                    Log.Logger.Information("Using Sqlite database at {dbLocation}", Path.GetFullPath(serverOptions.Database));
                    opt.UseSqlite($"Data Source='{serverOptions.Database}'");
                }
            }, ServiceLifetime.Scoped, ServiceLifetime.Singleton);

            services.AddSingleton<ISmtp4devServer, Smtp4devServer>();
            services.AddSingleton<ImapServer>();
            services.AddScoped<IMessagesRepository, MessagesRepository>();
            services.AddScoped<IHostingEnvironmentHelper, HostingEnvironmentHelper>();
            services.AddSingleton<ITaskQueue, TaskQueue>();


            services.AddSingleton<Func<RelayOptions, SmtpClient>>((relayOptions) =>
            {
                if (!relayOptions.IsEnabled)
                {
                    return null;
                }

                SmtpClient result = new SmtpClient();
                result.Connect(relayOptions.SmtpServer, relayOptions.SmtpPort, relayOptions.TlsMode);

                if (!string.IsNullOrEmpty(relayOptions.Login))
                {
                    result.Authenticate(relayOptions.Login, relayOptions.Password);
                }

                return result;
            });


            services.AddSignalR();
            services.AddSingleton<NotificationsHub>();

            services.AddControllers();
            services.AddOpenApiDocument(c =>
            {
                c.Title = "smtp4dev";
            });

        }


        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            ServerOptions serverOptions = Configuration.GetSection("ServerOptions").Get<ServerOptions>();

            app.UseRouting();


            Action<IApplicationBuilder> configure = subdir =>
            {
                if (env.IsDevelopment())
                {
                    app.UseWebAssemblyDebugging();
                }

                subdir.UseOpenApi();
                subdir.UseSwaggerUi3();

                subdir.UseRouting();
                subdir.UseDeveloperExceptionPage();
                subdir.UseDefaultFiles();
                subdir.UseBlazorFrameworkFiles();
                subdir.UseStaticFiles();

                subdir.UseWebSockets();

                subdir.UseEndpoints(e =>
                {
                    e.MapHub<NotificationsHub>("/hubs/notifications");
                    e.MapControllers();
                    e.MapFallbackToFile("index.html");

                });



                using (IServiceScope scope = subdir.ApplicationServices.CreateScope())
                {
                    using (Smtp4devDbContext context = scope.ServiceProvider.GetService<Smtp4devDbContext>())
                    {
                        if (!context.Database.IsInMemory())
                        {
                            context.Database.Migrate();
                        }
                    }
                }

                subdir.ApplicationServices.GetService<ISmtp4devServer>().TryStart();
                subdir.ApplicationServices.GetService<ImapServer>().TryStart();
            };

            if (!string.IsNullOrEmpty(serverOptions.BasePath) && serverOptions.BasePath != "/")
            {
                RewriteOptions rewrites = new RewriteOptions();
                rewrites.AddRedirect("^" + serverOptions.BasePath.TrimEnd('/') + "$", serverOptions.BasePath.TrimEnd('/') + "/"); ;
                rewrites.AddRedirect("^(/)?$", serverOptions.BasePath.TrimEnd('/') + "/"); ;
                app.UseRewriter(rewrites);

                app.Map(serverOptions.BasePath, configure);
            }
            else
            {
                configure(app);
            }
        }
    }
}
