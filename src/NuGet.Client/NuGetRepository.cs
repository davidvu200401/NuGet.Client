﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NuGet.Client.Diagnostics;
using NuGet.Client.Models;

namespace NuGet.Client
{
    /// <summary>
    /// Represents a connection to a single NuGet Repository
    /// </summary>
    public class NuGetRepository : IDisposable
    {
        private readonly ITraceSink _trace;
        private readonly HttpClient _rootClient;

        /// <summary>
        /// Gets the Root URL of the NuGet Repository in use.
        /// </summary>
        public Uri Url { get; private set; }

        /// <summary>
        /// Creates a client for the Repository at the specified URL.
        /// </summary>
        /// <param name="url">The URL to the root of the NuGet Repository.</param>
        public NuGetRepository(Uri url)
            : this(url, TraceSinks.Null)
        {
            Guard.NotNull(url, "url"); // Yes, the chained constructor will do this. This is here for Contracts.
        }

        /// <summary>
        /// Creates a client for the Repository at the specified URL.
        /// </summary>
        /// <param name="url">The URL to the root of the NuGet Repository.</param>
        /// <param name="trace">An <see cref="NuGet.Client.Diagnostics.ITraceSink"/> object that can be used to trace diagnostic events.</param>
        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "The object is captured for disposal later")]
        public NuGetRepository(Uri url, ITraceSink trace)
        {
            Guard.NotNull(url, "url");
            Guard.NotNull(trace, "trace");

            Url = url;
            _trace = trace;
            _rootClient = new HttpClient(new HttpClientHandler());
        }

        /// <summary>
        /// Releases the resources associated with this object.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Gets a description of the services available in this Repository.
        /// </summary>
        /// <returns>A <see cref="NuGet.Client.Models.RepositoryDescription"/> which describes the services available in this Repository.</returns>
        public Task<RepositoryDescription> GetRepositoryDescription()
        {
            return GetRepositoryDescription(CancellationToken.None);
        }

        /// <summary>
        /// Gets a description of the services available in this Repository.
        /// </summary>
        /// <param name="cancellationToken">A <see cref="System.Threading.CancellationToken"/> that can be used to cancel the request</param>
        /// <returns>A <see cref="NuGet.Client.Models.RepositoryDescription"/> which describes the services available in this Repository.</returns>
        public async Task<RepositoryDescription> GetRepositoryDescription(CancellationToken cancellationToken)
        {
            // Create a call context and invoke the core method
            using (var context = CreateContext(cancellationToken))
            {
                return await GetRepositoryDescription(context);
            }
        }

        private async Task<RepositoryDescription> GetRepositoryDescription(ServiceInvocationContext context)
        {
            // We use ConfigureAwait(false) to prevent .NET from trying to synchronize back to our current Sync Context (UI Thread/HttpContext/etc)
            // when continuing from await. We don't care about Sync Context, so this saves the perf hit of syncing back. Our caller will still 
            // sync back to their current Sync Context when they await us, so this doesn't affect them.
            using (context.Trace.EnterExit())
            {
                try
                {
                    // Invoke the request
                    var url = new Uri("/", UriKind.Relative);
                    var response = await context.GetAsync(url, context.CancellationToken)
                        .ConfigureAwait(continueOnCapturedContext: false);
                    context.CancellationToken.ThrowIfCancellationRequested();
                    response.EnsureSuccessStatusCode();

                    // Parse the response as JSON
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync()
                        .ConfigureAwait(continueOnCapturedContext: false));

                    // Load the json in to a result
                    return RepositoryDescription.FromJson(json, context.Trace, context.ResolveUrl(url));
                }
                catch (Exception ex)
                {
                    context.Trace.Error(ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Gets the description of a specific service, if it exists.
        /// </summary>
        /// <param name="serviceName">The name of the service to get</param>
        /// <returns>A <see cref="ServiceDescription"/> that describes the service, or null if no such service exists in this repository.</returns>
        public Task<ServiceDescription> GetService(string serviceName)
        {
            return GetService(serviceName, CancellationToken.None);
        }

        /// <summary>
        /// Gets the description of a specific service, if it exists.
        /// </summary>
        /// <param name="serviceName">The name of the service to get</param>
        /// <param name="cancellationToken">A <see cref="System.Threading.CancellationToken"/> that can be used to cancel the request</param>
        /// <returns>A <see cref="ServiceDescription"/> that describes the service, or null if no such service exists in this repository.</returns>
        public async Task<ServiceDescription> GetService(string serviceName, CancellationToken cancellationToken)
        {
            // Create a call context
            using (var context = CreateContext(cancellationToken))
            {
                return await GetService(serviceName, context);
            }
        }

        private async Task<ServiceDescription> GetService(string serviceName, ServiceInvocationContext context)
        {
            using (context.Trace.EnterExit())
            {
                // TODO: Caching!
                var repoDesc = await GetRepositoryDescription(context);
                context.CancellationToken.ThrowIfCancellationRequested();
                return repoDesc.Services.FirstOrDefault(svc => String.Equals(svc.Name, serviceName, StringComparison.OrdinalIgnoreCase));
            }
        }


        /// <summary>
        /// Creates a raw HTTP client for communicating with a specific service
        /// </summary>
        /// <param name="serviceName">The name of the service to create a client for</param>
        /// <returns></returns>
        public Task<ServiceClient> 
            CreateClient(string serviceName)
        {
            return CreateClient(serviceName, CancellationToken.None);
        }

        /// <summary>
        /// Creates a raw HTTP client for communicating with a specific service
        /// </summary>
        /// <param name="serviceName">The name of the service to create a client for</param>
        /// <param name="cancellationToken">A <see cref="System.Threading.CancellationToken"/> that can be used to cancel the request</param>
        /// <returns></returns>
        public async Task<ServiceClient> CreateClient(string serviceName, CancellationToken cancellationToken)
        {
            using (var context = CreateContext(cancellationToken))
            {
                return await CreateClient(serviceName, context);
            }
        }

        private async Task<ServiceClient> CreateClient(string serviceName, ServiceInvocationContext context)
        {
            using (context.Trace.EnterExit())
            {
                var service = await GetService(serviceName, context);
                return new ServiceClient(service, this);
            }
        }

        /// <summary>
        /// Releases the resources associated with this object.
        /// </summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _rootClient.Dispose();
            }
        }

        internal ServiceInvocationContext CreateContext(CancellationToken cancellationToken)
        {
            return new ServiceInvocationContext(_rootClient, _trace, Url, cancellationToken);
        }
    }
}