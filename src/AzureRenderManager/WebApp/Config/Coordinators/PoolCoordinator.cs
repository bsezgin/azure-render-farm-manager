﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Management.Batch;
using Microsoft.Azure.Management.Batch.Models;
using Microsoft.Azure.Management.Compute;
using Microsoft.Rest.Azure;
using TaskTupleAwaiter;
using WebApp.Code;
using WebApp.Code.Contract;
using WebApp.Config.Pools;
using WebApp.Operations;

using ImageReference = Microsoft.Azure.Batch.ImageReference;

namespace WebApp.Config.Coordinators
{
    public sealed class PoolCoordinator : NeedsAccessToken, IPoolCoordinator
    {
        private const string EnvironmentNotFound = "No environments are configured with the supplied environment ID";

        private readonly IManagementClientProvider _managementClient;
        private readonly BatchClientMsiProvider _batchClient;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public PoolCoordinator(
            IHttpContextAccessor httpContextAccessor,
            IManagementClientProvider managementClient,
            BatchClientMsiProvider batchClient)
            : base(httpContextAccessor)
        {
            _managementClient = managementClient;
            _batchClient = batchClient;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task CreatePool(RenderingEnvironment environment, Pool pool)
        {
            using (var client = await _managementClient.CreateBatchManagementClient(environment.SubscriptionId))
            {
                await client.Pool.CreateAsync(environment.BatchAccount.ResourceGroupName, environment.BatchAccount.Name, pool.Name, pool);
            }

            // TODO: add the pool object to the cache
            var cacheKey = CacheKeys.MakeKey(CacheKeys.PoolList, environment.Name);
            _httpContextAccessor.HttpContext.Session.Remove(cacheKey);
        }

        public async Task DeletePool(RenderingEnvironment environment, string poolName)
        {
            using (var client = await _managementClient.CreateBatchManagementClient(environment.SubscriptionId))
            {
                await client.Pool.BeginDeleteAsync(environment.BatchAccount.ResourceGroupName, environment.BatchAccount.Name, poolName);
            }

            // TODO: remove the pool object from the cache
            var cacheKey = CacheKeys.MakeKey(CacheKeys.PoolList, environment.Name);
            _httpContextAccessor.HttpContext.Session.Remove(cacheKey);
        }

        public async Task<Pool> GetPool(RenderingEnvironment environment, string poolName)
        {
            try
            {
                Pool result;
                using (var client = await _managementClient.CreateBatchManagementClient(environment.SubscriptionId))
                {
                    result = await client.Pool.GetAsync(environment.BatchAccount.ResourceGroupName, environment.BatchAccount.Name, poolName);
                }

                if (!IsPoolManaged(result))
                {
                    throw new InvalidOperationException("The specified pool is not managed by this portal");
                }

                return result;
            }
            catch (CloudException cEx)
            {
                if (cEx.Response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                Console.WriteLine(cEx);
                throw;
            }
        }

        public void ClearCache(string envId)
        {
            var cacheKey = CacheKeys.MakeKey(CacheKeys.PoolList, envId);
            _httpContextAccessor.HttpContext.Session.Remove(cacheKey);
        }

        public async Task<List<Pool>> ListPools(RenderingEnvironment environment)
        {
            var result = new List<Pool>();

            using (var client = await _managementClient.CreateBatchManagementClient(environment.SubscriptionId))
            {
                try
                {
                    var pools = await client.Pool.ListByBatchAccountAsync(environment.BatchAccount.ResourceGroupName, environment.BatchAccount.Name);
                    result.AddRange(pools.Where(IsPoolManaged));

                    while (pools.NextPageLink != null)
                    {
                        pools = await client.Pool.ListByBatchAccountNextAsync(pools.NextPageLink);
                        result.AddRange(pools.Where(IsPoolManaged));
                    }
                }
                catch (CloudException cEx)
                {
                    // TODO: What else to go here?
                    // TODO: Return typed error so we can report back to the UI
                    Console.WriteLine(cEx);
                }

                return result;
            }
        }


        public async Task<ImageReferences> GetImageReferences(RenderingEnvironment environment)
        {
            var result = new ImageReferences();

            // these write to different lists so it's okay to run them in parallel
            await (GetOfficialImageReferences(environment, result.SKUs, result.OfficialImages),
                GetCustomImageReferences(environment, result.CustomImages));

            return result;
        }

        public async Task UpdatePool(RenderingEnvironment environment, Pool pool)
        {
            using (var client = await _managementClient.CreateBatchManagementClient(environment.SubscriptionId))
            {
                await client.Pool.UpdateAsync(environment.BatchAccount.ResourceGroupName, environment.BatchAccount.Name, pool.Name, pool);
            }

            // clear the cache to reload the pools.
            // TODO: update the pool object in the cache
            var cacheKey = CacheKeys.MakeKey(CacheKeys.PoolList, environment.Name);
            _httpContextAccessor.HttpContext.Session.Remove(cacheKey);
        }

        private static bool IsPoolManaged(Pool pool)
        {
            return true;

            // TODO: Uncomment and use this instead. I want pools to test so ignoring this at the moment.
            // return pool.Metadata != null && pool.Metadata.Any(mi => mi.Name == MetadataKeys.Package);
        }

        private async Task GetOfficialImageReferences(RenderingEnvironment environment, List<string> skus, List<(string sku, ImageReference image)> images)
        {
            using (var client = _batchClient.CreateBatchClient(environment))
            {
                var fetched = client.PoolOperations.ListNodeAgentSkus().GetPagedEnumerator();
                while (await fetched.MoveNextAsync())
                {
                    var id = fetched.Current.Id;
                    skus.Add(id);
                    images.AddRange(fetched.Current.VerifiedImageReferences.Select(ir => (id, ir)));
                }
            }
        }

        private async Task GetCustomImageReferences(RenderingEnvironment environment, List<ImageReference> images)
        {
            using (var client = await _managementClient.CreateComputeManagementClient(environment.SubscriptionId))
            {
                var imageResults = await client.Images.ListAsync();
                images.AddRange(imageResults.Select(img => new ImageReference(img.Id)));

                while (imageResults.NextPageLink != null)
                {
                    imageResults = await client.Images.ListNextAsync(imageResults.NextPageLink);
                    images.AddRange(imageResults.Select(img => new ImageReference(img.Id)));
                }
            }
        }
    }
}