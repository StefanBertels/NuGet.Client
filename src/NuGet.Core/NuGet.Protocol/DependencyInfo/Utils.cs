// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet.Common;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NuGet.Protocol
{
    internal static class Utils
    {
        public static VersionRange CreateVersionRange(string stringToParse)
        {
            var range = VersionRange.Parse(string.IsNullOrEmpty(stringToParse) ? "[0.0.0-alpha,)" : stringToParse);
            return new VersionRange(range.MinVersion, range.IsMinInclusive, range.MaxVersion, range.IsMaxInclusive);
        }

        public async static Task<IEnumerable<JObject>> LoadRanges(
            HttpSource httpSource,
            Uri registrationUri,
            VersionRange range,
            ILogger log,
            CancellationToken token)
        {
            using (var sourecCacheContext = new SourceCacheContext())
            {
                var httpSourceCacheContext = HttpSourceCacheContext.Create(sourecCacheContext, 0);

                var parts = registrationUri.OriginalString.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                var packageId = parts[parts.Length - 2];

                var index = await httpSource.GetAsync(
                    new HttpSourceCachedRequest(
                        registrationUri.OriginalString,
                        $"list_{packageId}_index",
                        httpSourceCacheContext)
                    {
                        IgnoreNotFounds = true,
                    },
                    async httpSourceResult =>
                    {
                        return await httpSourceResult.Stream.AsJObjectAsync();
                    },
                    log,
                    token);

                if (index == null)
                {
                    // The server returned a 404, the package does not exist
                    return Enumerable.Empty<JObject>();
                }

                IList<Task<JObject>> rangeTasks = new List<Task<JObject>>();

                foreach (JObject item in index["items"])
                {
                    var lower = NuGetVersion.Parse(item["lower"].ToString());
                    var upper = NuGetVersion.Parse(item["upper"].ToString());

                    if (IsItemRangeRequired(range, lower, upper))
                    {
                        JToken items;
                        if (!item.TryGetValue("items", out items))
                        {
                            var rangeUri = item["@id"].ToString();

                            rangeTasks.Add(httpSource.GetAsync(
                                new HttpSourceCachedRequest(
                                    rangeUri,
                                    $"list_{packageId}_range_{lower.ToNormalizedString()}-{upper.ToNormalizedString()}",
                                    httpSourceCacheContext)
                                {
                                    IgnoreNotFounds = true,
                                },
                                async httpSourceResult =>
                                {
                                    return await httpSourceResult.Stream.AsJObjectAsync();
                                },
                                log,
                                token));
                        }
                        else
                        {
                            rangeTasks.Add(Task.FromResult(item));
                        }
                    }
                }

                await Task.WhenAll(rangeTasks.ToArray());

                return rangeTasks.Select((t) => t.Result);
            }
        }

        private static bool IsItemRangeRequired(VersionRange dependencyRange, NuGetVersion catalogItemLower, NuGetVersion catalogItemUpper)
        {
            var catalogItemVersionRange = new VersionRange(minVersion: catalogItemLower, includeMinVersion: true,
                maxVersion: catalogItemUpper, includeMaxVersion: true);

            if (dependencyRange.HasLowerAndUpperBounds) // Mainly to cover the '!dependencyRange.IsMaxInclusive && !dependencyRange.IsMinInclusive' case
            {
                return catalogItemVersionRange.Satisfies(dependencyRange.MinVersion) || catalogItemVersionRange.Satisfies(dependencyRange.MaxVersion);
            }
            else
            {
                return dependencyRange.Satisfies(catalogItemLower) || dependencyRange.Satisfies(catalogItemUpper);
            }
        }
    }
}
