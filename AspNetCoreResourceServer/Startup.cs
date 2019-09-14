using AspNetCoreResourceServer.Model;
using AspNetCoreResourceServer.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using IdentityServer4.AccessTokenValidation;
using Microsoft.AspNetCore.Mvc;
using AspNetCoreResourceServer.DataProtection;
using System;
using AspNetCoreResourceServer.Certificate;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Logging;

namespace AspNetCoreResourceServer
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment webHostEnvironment)
        {
            Configuration = configuration;
            _webHostEnvironment = webHostEnvironment;
        }

        public IConfiguration Configuration { get; }
        private readonly IWebHostEnvironment _webHostEnvironment;

        public void ConfigureServices(IServiceCollection services)
        {
            var connection = Configuration.GetConnectionString("DefaultConnection");
            var useLocalCertStore = Convert.ToBoolean(Configuration["UseLocalCertStore"]);
            var certificateThumbprint = Configuration["CertificateThumbprint"];

            X509Certificate2 cert;

            if (_webHostEnvironment.IsProduction())
            {
                if (useLocalCertStore)
                {
                    using (X509Store store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
                    {
                        store.Open(OpenFlags.ReadOnly);
                        var certs = store.Certificates.Find(X509FindType.FindByThumbprint, certificateThumbprint, false);
                        cert = certs[0];
                        store.Close();
                    }
                }
                else
                {
                    // Azure deployment, will be used if deployed to Azure
                    var vaultConfigSection = Configuration.GetSection("Vault");
                    var keyVaultService = new KeyVaultCertificateService(vaultConfigSection["Url"], vaultConfigSection["ClientId"], vaultConfigSection["ClientSecret"]);
                    cert = keyVaultService.GetCertificateFromKeyVault(vaultConfigSection["CertificateName"]);
                }
            }
            else
            {
                cert = new X509Certificate2(Path.Combine(_webHostEnvironment.ContentRootPath, "damienbodserver.pfx"), "");
            }

            // Important The folderForKeyStore needs to be backed up.
            // services.AddDataProtection()
            //    .SetApplicationName("ResourceServer")
            //    .PersistKeysToFileSystem(new DirectoryInfo(folderForKeyStore))
            //    .ProtectKeysWithCertificate(cert);

            services.AddDataProtection()
                .SetApplicationName("ResourceServer")
                .ProtectKeysWithCertificate(cert)
                .AddKeyManagementOptions(options =>
                    options.XmlRepository = new SqlXmlRepository(
                        new DataProtectionDbContext(
                            new DbContextOptionsBuilder<DataProtectionDbContext>().UseSqlite(connection).Options
                        )
                    )
                );

            services.AddDbContext<DataEventRecordContext>(options =>
                options.UseSqlite(connection)
            );

            var policy = new Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicy();

            policy.Headers.Add("*");
            policy.Methods.Add("*");
            policy.Origins.Add("*");
            policy.SupportsCredentials = true;

            services.AddCors(x => x.AddPolicy("corsGlobalPolicy", policy));

            var guestPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim("scope", "dataEventRecords")
                .Build();

            services.AddAuthentication(IdentityServerAuthenticationDefaults.AuthenticationScheme)
              .AddIdentityServerAuthentication(options =>
              {
                  options.Authority = "https://localhost:44318/";
                  options.ApiName = "dataEventRecords";
                  options.ApiSecret = "dataEventRecordsSecret";
              });

            services.AddAuthorization(options =>
            {
                options.AddPolicy("dataEventRecordsAdmin", policyAdmin =>
                {
                    policyAdmin.RequireClaim("role", "dataEventRecords.admin");
                });
                options.AddPolicy("dataEventRecordsUser", policyUser =>
                {
                    policyUser.RequireClaim("role",  "dataEventRecords.user");
                });
                options.AddPolicy("dataEventRecords", policyUser =>
                {
                    policyUser.RequireClaim("scope", "dataEventRecords");
                });
            });

            services.AddControllers()
               .AddNewtonsoftJson()
               .AddJsonOptions(options =>
               {
                    //options.JsonSerializerOptions.ContractResolver = new DefaultContractResolver();
                })
               .SetCompatibilityVersion(CompatibilityVersion.Version_3_0);

            services.AddScoped<IDataEventRecordRepository, DataEventRecordRepository>();
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseCors("corsGlobalPolicy");
           
            //JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

            app.UseStaticFiles();
            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }
}
