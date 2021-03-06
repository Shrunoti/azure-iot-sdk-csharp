﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.Devices
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Common;
    using Microsoft.Azure.Devices.Common.Exceptions;
    using Microsoft.Azure.Devices.Shared;
    using Newtonsoft.Json;

    class HttpRegistryManager : RegistryManager
    {
        const string AdminUriFormat = "/$admin/{0}?{1}";
        const string RequestUriFormat = "/devices/{0}?{1}";
        const string JobsUriFormat = "/jobs{0}?{1}";
        const string StatisticsUriFormat = "/statistics/devices?" + ClientApiVersionHelper.ApiVersionQueryString;
        const string DevicesRequestUriFormat = "/devices/?top={0}&{1}";
        const string DevicesQueryUriFormat = "/devices/query?" + ClientApiVersionHelper.ApiVersionQueryString;
        const string WildcardEtag = "*";

        const string ContinuationTokenHeader = "x-ms-continuation";
        const string PageSizeHeader = "x-ms-max-item-count";

        const string TwinUriFormat = "/twins/{0}?{1}";
        const string TwinTagsUriFormat = "/twins/{0}/tags?{1}";
        const string TwinDesiredPropertiesUriFormat = "/twins/{0}/properties/desired?{1}";

        static readonly Regex DeviceIdRegex = new Regex(@"^[A-Za-z0-9\-:.+%_#*?!(),=@;$']{1,128}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(100);
        static readonly TimeSpan DefaultGetDevicesOperationTimeout = TimeSpan.FromSeconds(120);

        IHttpClientHelper httpClientHelper;
        readonly string iotHubName;

        internal HttpRegistryManager(IotHubConnectionString connectionString)
        {
            this.iotHubName = connectionString.IotHubName;
            this.httpClientHelper = new HttpClientHelper(
                connectionString.HttpsEndpoint,
                connectionString,
                ExceptionHandlingHelper.GetDefaultErrorMapping(),
                DefaultOperationTimeout,
                client => { });
        }

        // internal test helper
        internal HttpRegistryManager(IHttpClientHelper httpClientHelper, string iotHubName)
        {
            if (httpClientHelper == null)
            {
                throw new ArgumentNullException(nameof(httpClientHelper));
            }

            this.iotHubName = iotHubName;
            this.httpClientHelper = httpClientHelper;
        }

        public override Task OpenAsync()
        {
            return TaskHelpers.CompletedTask;
        }

        public override Task CloseAsync()
        {
            return TaskHelpers.CompletedTask;
        }

        public override Task<Device> AddDeviceAsync(Device device)
        {
            return this.AddDeviceAsync(device, CancellationToken.None);
        }

        public override Task<Device> AddDeviceAsync(Device device, CancellationToken cancellationToken)
        {
            this.EnsureInstanceNotClosed();

            ValidateDeviceId(device);

            if (!string.IsNullOrEmpty(device.ETag))
            {
                throw new ArgumentException(ApiResources.ETagSetWhileRegisteringDevice);
            }

            ValidateDeviceAuthentication(device.Authentication, device.Id);

            NormalizeDevice(device);

            var errorMappingOverrides = new Dictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>>
            {
                {
                    HttpStatusCode.PreconditionFailed,
                    async responseMessage => new PreconditionFailedException(await ExceptionHandlingHelper.GetExceptionMessageAsync(responseMessage).ConfigureAwait(false))
                }
            };

            return this.httpClientHelper.PutAsync(GetRequestUri(device.Id), device, PutOperationType.CreateEntity, errorMappingOverrides, cancellationToken);
        }

        public override Task<BulkRegistryOperationResult> AddDeviceWithTwinAsync(Device device, Twin twin)
        {
            return AddDeviceWithTwinAsync(device, twin, CancellationToken.None);
        }

        public override Task<BulkRegistryOperationResult> AddDeviceWithTwinAsync(Device device, Twin twin, CancellationToken cancellationToken)
        {
            ValidateDeviceId(device);
            if (!string.IsNullOrWhiteSpace(device.ETag))
            {
                throw new ArgumentException(ApiResources.ETagSetWhileRegisteringDevice);
            }
            var exportImportDeviceList = new List<ExportImportDevice>(1);

            var exportImportDevice = new ExportImportDevice(device, ImportMode.Create);
            exportImportDevice.Tags = twin?.Tags;
            exportImportDevice.Properties =  new ExportImportDevice.PropertyContainer();
            exportImportDevice.Properties.DesiredProperties = twin?.Properties.Desired;
            exportImportDevice.Properties.ReportedProperties = twin?.Properties.Reported;

            exportImportDeviceList.Add(exportImportDevice);

            return this.BulkDeviceOperationsAsync<BulkRegistryOperationResult>(
               exportImportDeviceList,
               ClientApiVersionHelper.ApiVersionQueryString,
               cancellationToken);
        }

        public override Task<string[]> AddDevicesAsync(IEnumerable<Device> devices)
        {
            return this.AddDevicesAsync(devices, CancellationToken.None);
        }

        public override Task<string[]> AddDevicesAsync(IEnumerable<Device> devices, CancellationToken cancellationToken)
        {
            return this.BulkDeviceOperationsAsync<string[]>(
                GenerateExportImportDeviceListForBulkOperations(devices, ImportMode.Create),
                ClientApiVersionHelper.ApiVersionQueryStringGA,
                cancellationToken);
        }

        public override Task<BulkRegistryOperationResult> AddDevices2Async(IEnumerable<Device> devices)
        {
            return this.AddDevices2Async(devices, CancellationToken.None);
        }

        public override Task<BulkRegistryOperationResult> AddDevices2Async(IEnumerable<Device> devices, CancellationToken cancellationToken)
        {
            return this.BulkDeviceOperationsAsync<BulkRegistryOperationResult>(
                GenerateExportImportDeviceListForBulkOperations(devices, ImportMode.Create),
                ClientApiVersionHelper.ApiVersionQueryString,
                cancellationToken);
        }

        public override Task<Device> UpdateDeviceAsync(Device device)
        {
            return this.UpdateDeviceAsync(device, CancellationToken.None);
        }

        public override Task<Device> UpdateDeviceAsync(Device device, bool forceUpdate)
        {
            return this.UpdateDeviceAsync(device, forceUpdate, CancellationToken.None);
        }

        public override Task<Device> UpdateDeviceAsync(Device device, CancellationToken cancellationToken)
        {
            return this.UpdateDeviceAsync(device, false, cancellationToken);
        }

        public override Task<Device> UpdateDeviceAsync(Device device, bool forceUpdate, CancellationToken cancellationToken)
        {
            this.EnsureInstanceNotClosed();

            ValidateDeviceId(device);

            if (string.IsNullOrWhiteSpace(device.ETag) && !forceUpdate)
            {
                throw new ArgumentException(ApiResources.ETagNotSetWhileUpdatingDevice);
            }

            ValidateDeviceAuthentication(device.Authentication, device.Id);

            NormalizeDevice(device);

            var errorMappingOverrides = new Dictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>>()
            {
                { HttpStatusCode.PreconditionFailed, async (responseMessage) => new PreconditionFailedException(await ExceptionHandlingHelper.GetExceptionMessageAsync(responseMessage).ConfigureAwait(false)) },
                {
                    HttpStatusCode.NotFound, async responseMessage =>
                    {
                        string responseContent = await ExceptionHandlingHelper.GetExceptionMessageAsync(responseMessage).ConfigureAwait(false);
                        return (Exception)new DeviceNotFoundException(responseContent, (Exception)null);
                    }
                }

            };

            PutOperationType operationType = forceUpdate ? PutOperationType.ForceUpdateEntity : PutOperationType.UpdateEntity;

            return this.httpClientHelper.PutAsync(GetRequestUri(device.Id), device, operationType, errorMappingOverrides, cancellationToken);
        }

        public override Task<string[]> UpdateDevicesAsync(IEnumerable<Device> devices)
        {
            return this.UpdateDevicesAsync(devices, false, CancellationToken.None);
        }

        public override Task<string[]> UpdateDevicesAsync(IEnumerable<Device> devices, bool forceUpdate, CancellationToken cancellationToken)
        {
            return this.BulkDeviceOperationsAsync<string[]>(
                GenerateExportImportDeviceListForBulkOperations(devices, forceUpdate ? ImportMode.Update : ImportMode.UpdateIfMatchETag),
                ClientApiVersionHelper.ApiVersionQueryStringGA,
                cancellationToken);
        }

        public override Task<BulkRegistryOperationResult> UpdateDevices2Async(IEnumerable<Device> devices)
        {
            return this.UpdateDevices2Async(devices, false, CancellationToken.None);
        }

        public override Task<BulkRegistryOperationResult> UpdateDevices2Async(IEnumerable<Device> devices, bool forceUpdate, CancellationToken cancellationToken)
        {
            return this.BulkDeviceOperationsAsync<BulkRegistryOperationResult>(
                GenerateExportImportDeviceListForBulkOperations(devices, forceUpdate ? ImportMode.Update : ImportMode.UpdateIfMatchETag),
                ClientApiVersionHelper.ApiVersionQueryString,
                cancellationToken);
        }

        public override Task RemoveDeviceAsync(string deviceId)
        {
            return this.RemoveDeviceAsync(deviceId, CancellationToken.None);
        }

        public override Task RemoveDeviceAsync(string deviceId, CancellationToken cancellationToken)
        {
            this.EnsureInstanceNotClosed();

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                throw new ArgumentException(IotHubApiResources.GetString(ApiResources.ParameterCannotBeNullOrWhitespace, "deviceId"));
            }

            // use wildcard etag
            var eTag = new ETagHolder { ETag = "*" };
            return this.RemoveDeviceAsync(deviceId, eTag, cancellationToken);
        }

        public override Task RemoveDeviceAsync(Device device)
        {
            return this.RemoveDeviceAsync(device, CancellationToken.None);
        }

        public override Task RemoveDeviceAsync(Device device, CancellationToken cancellationToken)
        {
            this.EnsureInstanceNotClosed();

            ValidateDeviceId(device);

            if (string.IsNullOrWhiteSpace(device.ETag))
            {
                throw new ArgumentException(ApiResources.ETagNotSetWhileDeletingDevice);
            }

            return this.RemoveDeviceAsync(device.Id, device, cancellationToken);
        }

        public override Task<string[]> RemoveDevicesAsync(IEnumerable<Device> devices)
        {
            return this.RemoveDevicesAsync(devices, false, CancellationToken.None);
        }

        public override Task<string[]> RemoveDevicesAsync(IEnumerable<Device> devices, bool forceRemove, CancellationToken cancellationToken)
        {
            return this.BulkDeviceOperationsAsync<string[]>(
                GenerateExportImportDeviceListForBulkOperations(devices, forceRemove ? ImportMode.Delete : ImportMode.DeleteIfMatchETag),
                ClientApiVersionHelper.ApiVersionQueryStringGA,
                cancellationToken);
        }

        public override Task<BulkRegistryOperationResult> RemoveDevices2Async(IEnumerable<Device> devices)
        {
            return this.RemoveDevices2Async(devices, false, CancellationToken.None);
        }

        public override Task<BulkRegistryOperationResult> RemoveDevices2Async(IEnumerable<Device> devices, bool forceRemove, CancellationToken cancellationToken)
        {
            return this.BulkDeviceOperationsAsync<BulkRegistryOperationResult>(
                GenerateExportImportDeviceListForBulkOperations(devices, forceRemove ? ImportMode.Delete : ImportMode.DeleteIfMatchETag),
                ClientApiVersionHelper.ApiVersionQueryString,
                cancellationToken);
        }

        public override Task<RegistryStatistics> GetRegistryStatisticsAsync()
        {
            return this.GetRegistryStatisticsAsync(CancellationToken.None);
        }

        public override Task<RegistryStatistics> GetRegistryStatisticsAsync(CancellationToken cancellationToken)
        {
            this.EnsureInstanceNotClosed();
            var errorMappingOverrides = new Dictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>>()
            {
                { HttpStatusCode.NotFound, responseMessage => Task.FromResult((Exception)new IotHubNotFoundException(this.iotHubName)) }
            };

            return this.httpClientHelper.GetAsync<RegistryStatistics>(GetStatisticsUri(), errorMappingOverrides, null, cancellationToken);
        }

        public override Task<Device> GetDeviceAsync(string deviceId)
        {
            return this.GetDeviceAsync(deviceId, CancellationToken.None);
        }

        public override Task<Device> GetDeviceAsync(string deviceId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                throw new ArgumentException(IotHubApiResources.GetString(ApiResources.ParameterCannotBeNullOrWhitespace, "deviceId"));
            }

            this.EnsureInstanceNotClosed();
            var errorMappingOverrides = new Dictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>>()
            {
                { HttpStatusCode.NotFound, async responseMessage => new DeviceNotFoundException(await ExceptionHandlingHelper.GetExceptionMessageAsync(responseMessage).ConfigureAwait(false)) }
            };

            return this.httpClientHelper.GetAsync<Device>(GetRequestUri(deviceId), errorMappingOverrides, null, false, cancellationToken);
        }

        [Obsolete("Use CreateQuery(\"select * from devices\", pageSize);")]
        public override Task<IEnumerable<Device>> GetDevicesAsync(int maxCount)
        {
            return this.GetDevicesAsync(maxCount, CancellationToken.None);
        }

        [Obsolete("Use CreateQuery(\"select * from devices\", pageSize);")]
        public override Task<IEnumerable<Device>> GetDevicesAsync(int maxCount, CancellationToken cancellationToken)
        {
            this.EnsureInstanceNotClosed();

            return this.httpClientHelper.GetAsync<IEnumerable<Device>>(
                GetDevicesRequestUri(maxCount),
                DefaultGetDevicesOperationTimeout,
                null,
                null,
                true,
                cancellationToken);
        }

        public override IQuery CreateQuery(string sqlQueryString)
        {
            return CreateQuery(sqlQueryString, null);
        }

        public override IQuery CreateQuery(string sqlQueryString, int? pageSize)
        {
            return new Query((token) => this.ExecuteQueryAsync(
                sqlQueryString,
                pageSize,
                token,
                CancellationToken.None));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && this.httpClientHelper != null)
            {
                this.httpClientHelper.Dispose();
                this.httpClientHelper = null;
            }
        } 

        static IEnumerable<ExportImportDevice> GenerateExportImportDeviceListForBulkOperations(IEnumerable<Device> devices, ImportMode importMode)
        {
            if (devices == null)
            {
                throw new ArgumentNullException(nameof(devices));
            }

            if (!devices.Any())
            {
                throw new ArgumentException(nameof(devices));
            }

            var exportImportDeviceList = new List<ExportImportDevice>(devices.Count());
            foreach (Device device in devices)
            {
                ValidateDeviceId(device);

                switch (importMode)
                {
                    case ImportMode.Create:
                        if (!string.IsNullOrWhiteSpace(device.ETag))
                        {
                            throw new ArgumentException(ApiResources.ETagSetWhileRegisteringDevice);
                        }
                        break;

                    case ImportMode.Update:
                        // No preconditions
                        break;

                    case ImportMode.UpdateIfMatchETag:
                        if (string.IsNullOrWhiteSpace(device.ETag))
                        {
                            throw new ArgumentException(ApiResources.ETagNotSetWhileUpdatingDevice);
                        }
                        break;

                    case ImportMode.Delete:
                        // No preconditions
                        break;

                    case ImportMode.DeleteIfMatchETag:
                        if (string.IsNullOrWhiteSpace(device.ETag))
                        {
                            throw new ArgumentException(ApiResources.ETagNotSetWhileDeletingDevice);
                        }
                        break;

                    default:
                        throw new ArgumentException(IotHubApiResources.GetString(ApiResources.InvalidImportMode, importMode));
                }

                var exportImportDevice = new ExportImportDevice(device, importMode);
                exportImportDeviceList.Add(exportImportDevice);
            }

            return exportImportDeviceList;
        }

        static IEnumerable<ExportImportDevice> GenerateExportImportDeviceListForTwinBulkOperations(IEnumerable<Twin> twins, ImportMode importMode)
        {
            if (twins == null)
            {
                throw new ArgumentNullException(nameof(twins));
            }

            if (!twins.Any())
            {
                throw new ArgumentException(nameof(twins));
            }

            var exportImportDeviceList = new List<ExportImportDevice>(twins.Count());
            foreach (Twin twin in twins)
            {
                ValidateTwinId(twin);

                switch (importMode)
                {
                    case ImportMode.UpdateTwin:
                        // No preconditions
                        break;

                    case ImportMode.UpdateTwinIfMatchETag:
                        if (string.IsNullOrWhiteSpace(twin.ETag))
                        {
                            throw new ArgumentException(ApiResources.ETagNotSetWhileUpdatingTwin);
                        }
                        break;

                    default:
                        throw new ArgumentException(IotHubApiResources.GetString(ApiResources.InvalidImportMode, importMode));
                }

                var exportImportDevice = new ExportImportDevice();
                exportImportDevice.Id = twin.DeviceId;
                exportImportDevice.ImportMode = importMode;
                exportImportDevice.TwinETag = importMode == ImportMode.UpdateTwinIfMatchETag ? twin.ETag : null;
                exportImportDevice.Tags = twin.Tags;
                exportImportDevice.Properties = new ExportImportDevice.PropertyContainer();
                exportImportDevice.Properties.DesiredProperties = twin.Properties?.Desired;

                exportImportDeviceList.Add(exportImportDevice);
            }

            return exportImportDeviceList;
        }

        Task<T> BulkDeviceOperationsAsync<T>(IEnumerable<ExportImportDevice> devices, string version, CancellationToken cancellationToken)
        {
            this.BulkDeviceOperationSetup(devices);

            var errorMappingOverrides = new Dictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>>
            {
                { HttpStatusCode.PreconditionFailed, async responseMessage => new PreconditionFailedException(await ExceptionHandlingHelper.GetExceptionMessageAsync(responseMessage).ConfigureAwait(false)) },
                { HttpStatusCode.RequestEntityTooLarge, async responseMessage => new TooManyDevicesException(await ExceptionHandlingHelper.GetExceptionMessageAsync(responseMessage).ConfigureAwait(false)) },
                { HttpStatusCode.BadRequest, async responseMessage => new ArgumentException(await ExceptionHandlingHelper.GetExceptionMessageAsync(responseMessage).ConfigureAwait(false)) }
            };

            return this.httpClientHelper.PostAsync<IEnumerable<ExportImportDevice>, T>(GetBulkRequestUri(version), devices, errorMappingOverrides, null, cancellationToken);
        }

        void BulkDeviceOperationSetup(IEnumerable<ExportImportDevice> devices)
        {
            this.EnsureInstanceNotClosed();

            if (devices == null)
            {
                throw new ArgumentNullException(nameof(devices));
            }

            foreach (ExportImportDevice device in devices)
            {
                ValidateDeviceAuthentication(device.Authentication, device.Id);

                NormalizeExportImportDevice(device);
            }

        }

        public override Task ExportRegistryAsync(string storageAccountConnectionString, string containerName)
        {
            return this.ExportRegistryAsync(storageAccountConnectionString, containerName, CancellationToken.None);
        }

        public override Task ExportRegistryAsync(string storageAccountConnectionString, string containerName, CancellationToken cancellationToken)
        {
            this.EnsureInstanceNotClosed();

            var errorMappingOverrides = new Dictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>>
            {
                { HttpStatusCode.NotFound, responseMessage => Task.FromResult((Exception)new IotHubNotFoundException(this.iotHubName)) }
            };

            return this.httpClientHelper.PostAsync(
                GetAdminUri("exportRegistry"),
                new ExportImportRequest
                {
                    ContainerName = containerName,
                    StorageConnectionString = storageAccountConnectionString
                },
                errorMappingOverrides,
                null,
                cancellationToken);
        }

        public override Task ImportRegistryAsync(string storageAccountConnectionString, string containerName)
        {
            return this.ImportRegistryAsync(storageAccountConnectionString, containerName, CancellationToken.None);
        }

        public override Task ImportRegistryAsync(string storageAccountConnectionString, string containerName, CancellationToken cancellationToken)
        {
            this.EnsureInstanceNotClosed();

            var errorMappingOverrides = new Dictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>>
            {
                { HttpStatusCode.NotFound, responseMessage => Task.FromResult((Exception)new IotHubNotFoundException(this.iotHubName)) }
            };

            return this.httpClientHelper.PostAsync(
                GetAdminUri("importRegistry"),
                new ExportImportRequest
                {
                    ContainerName = containerName,
                    StorageConnectionString = storageAccountConnectionString
                },
                errorMappingOverrides,
                null,
                cancellationToken);
        }

        public override Task<JobProperties> GetJobAsync(string jobId)
        {
            return this.GetJobAsync(jobId, CancellationToken.None);
        }

        public override Task<IEnumerable<JobProperties>> GetJobsAsync()
        {
            return this.GetJobsAsync(CancellationToken.None);
        }

        public override Task CancelJobAsync(string jobId)
        {
            return this.CancelJobAsync(jobId, CancellationToken.None);
        }

        public override Task<JobProperties> ExportDevicesAsync(string exportBlobContainerUri, bool excludeKeys)
        {
            return this.ExportDevicesAsync(exportBlobContainerUri, excludeKeys, CancellationToken.None);
        }

        public override Task<JobProperties> ExportDevicesAsync(string exportBlobContainerUri, bool excludeKeys, CancellationToken ct)
        {
            return this.ExportDevicesAsync(exportBlobContainerUri, null, excludeKeys, ct);
        }

        public override Task<JobProperties> ExportDevicesAsync(string exportBlobContainerUri, string outputBlobName, bool excludeKeys)
        {
            return this.ExportDevicesAsync(exportBlobContainerUri, outputBlobName, excludeKeys, CancellationToken.None);
        }

        public override Task<JobProperties> ExportDevicesAsync(string exportBlobContainerUri, string outputBlobName, bool excludeKeys, CancellationToken ct)
        {
            var jobProperties = new JobProperties()
            {
                Type = JobType.ExportDevices,
                OutputBlobContainerUri = exportBlobContainerUri,
                ExcludeKeysInExport = excludeKeys,
                OutputBlobName = outputBlobName
            };

            return this.CreateJobAsync(jobProperties, ct);
        }

        public override Task<JobProperties> ImportDevicesAsync(string importBlobContainerUri, string outputBlobContainerUri)
        {
            return this.ImportDevicesAsync(importBlobContainerUri, outputBlobContainerUri, CancellationToken.None);
        }

        public override Task<JobProperties> ImportDevicesAsync(string importBlobContainerUri, string outputBlobContainerUri, CancellationToken ct)
        {
            return this.ImportDevicesAsync(importBlobContainerUri, outputBlobContainerUri, null, ct);
        }

        public override Task<JobProperties> ImportDevicesAsync(string importBlobContainerUri, string outputBlobContainerUri, string inputBlobName)
        {
            return this.ImportDevicesAsync(importBlobContainerUri, outputBlobContainerUri, inputBlobName, CancellationToken.None);
        }

        public override Task<JobProperties> ImportDevicesAsync(string importBlobContainerUri, string outputBlobContainerUri, string inputBlobName, CancellationToken ct)
        {
            var jobProperties = new JobProperties()
            {
                Type = JobType.ImportDevices,
                InputBlobContainerUri = importBlobContainerUri,
                OutputBlobContainerUri = outputBlobContainerUri,
                InputBlobName = inputBlobName
            };

            return this.CreateJobAsync(jobProperties, ct);
        }

        Task<JobProperties> CreateJobAsync(JobProperties jobProperties, CancellationToken ct)
        {
            this.EnsureInstanceNotClosed();

            var errorMappingOverrides = new Dictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>>
            {
                { HttpStatusCode.Forbidden, responseMessage => Task.FromResult((Exception) new JobQuotaExceededException())}
            };

            return this.httpClientHelper.PostAsync<JobProperties, JobProperties>(
                GetJobUri("/create"),
                jobProperties,
                errorMappingOverrides,
                null,
                ct);
        }

        public override Task<JobProperties> GetJobAsync(string jobId, CancellationToken cancellationToken)
        {
            this.EnsureInstanceNotClosed();

            var errorMappingOverrides = new Dictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>>
            {
                { HttpStatusCode.NotFound, responseMessage => Task.FromResult((Exception)new JobNotFoundException(jobId)) }
            };

            return this.httpClientHelper.GetAsync<JobProperties>(
                GetJobUri("/{0}".FormatInvariant(jobId)),
                errorMappingOverrides,
                null,
                cancellationToken);
        }

        public override Task<IEnumerable<JobProperties>> GetJobsAsync(CancellationToken cancellationToken)
        {
            this.EnsureInstanceNotClosed();

            return this.httpClientHelper.GetAsync<IEnumerable<JobProperties>>(
                GetJobUri(string.Empty),
                null,
                null,
                cancellationToken);
        }

        public override Task CancelJobAsync(string jobId, CancellationToken cancellationToken)
        {
            this.EnsureInstanceNotClosed();

            var errorMappingOverrides = new Dictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>>
            {
                { HttpStatusCode.NotFound, responseMessage => Task.FromResult((Exception)new JobNotFoundException(jobId)) }
            };

            IETagHolder jobETag = new ETagHolder()
            {
                ETag = jobId
            };

            return this.httpClientHelper.DeleteAsync(
                GetJobUri("/{0}".FormatInvariant(jobId)),
                jobETag,
                errorMappingOverrides,
                null,
                cancellationToken);
        }

        public override Task<Twin> GetTwinAsync(string deviceId)
        {
            return this.GetTwinAsync(deviceId, CancellationToken.None);
        }

        public override Task<Twin> GetTwinAsync(string deviceId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                throw new ArgumentException(IotHubApiResources.GetString(ApiResources.ParameterCannotBeNullOrWhitespace, "deviceId"));
            }

            this.EnsureInstanceNotClosed();
            var errorMappingOverrides = new Dictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>>()
            {
                { HttpStatusCode.NotFound, async responseMessage => new DeviceNotFoundException(await ExceptionHandlingHelper.GetExceptionMessageAsync(responseMessage).ConfigureAwait(false)) }
            };

            return this.httpClientHelper.GetAsync<Twin>(GetTwinUri(deviceId), errorMappingOverrides, null, false, cancellationToken);
        }

        public override Task<Twin> UpdateTwinAsync(string deviceId, string jsonTwinPatch, string etag)
        {
            return this.UpdateTwinAsync(deviceId, jsonTwinPatch, etag, CancellationToken.None);
        }

        public override Task<Twin> UpdateTwinAsync(string deviceId, string jsonTwinPatch, string etag, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(jsonTwinPatch))
            {
                throw new ArgumentNullException(nameof(jsonTwinPatch));
            }

            // TODO: Do we need to deserialize Twin, only to serialize it again?
            var twin = JsonConvert.DeserializeObject<Twin>(jsonTwinPatch);
            return this.UpdateTwinAsync(deviceId, twin, etag, cancellationToken);
        }

        public override Task<Twin> UpdateTwinAsync(string deviceId, Twin twinPatch, string etag)
        {
            return this.UpdateTwinAsync(deviceId, twinPatch, etag, CancellationToken.None);
        }

        public override Task<Twin> UpdateTwinAsync(string deviceId, Twin twinPatch, string etag, CancellationToken cancellationToken)
        {
            return this.UpdateTwinInternalAsync(deviceId, twinPatch, etag, cancellationToken, false);
        }

        public override Task<Twin> ReplaceTwinAsync(string deviceId, string newTwinJson, string etag)
        {
            return this.ReplaceTwinAsync(deviceId, newTwinJson, etag, CancellationToken.None);
        }

        public override Task<Twin> ReplaceTwinAsync(string deviceId, string newTwinJson, string etag, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(newTwinJson))
            {
                throw new ArgumentNullException(nameof(newTwinJson));
            }

            // TODO: Do we need to deserialize Twin, only to serialize it again?
            var twin = JsonConvert.DeserializeObject<Twin>(newTwinJson);
            return this.ReplaceTwinAsync(deviceId, twin, etag, cancellationToken);
        }

        public override Task<Twin> ReplaceTwinAsync(string deviceId, Twin newTwin, string etag)
        {
            return this.ReplaceTwinAsync(deviceId, newTwin, etag, CancellationToken.None);
        }

        public override Task<Twin> ReplaceTwinAsync(string deviceId, Twin newTwin, string etag, CancellationToken cancellationToken)
        {
            return this.UpdateTwinInternalAsync(deviceId, newTwin, etag, cancellationToken, true);
        }

        Task<Twin> UpdateTwinInternalAsync(string deviceId, Twin twin, string etag, CancellationToken cancellationToken, bool isReplace)
        {
            this.EnsureInstanceNotClosed();

            if (twin != null)
            {
                twin.DeviceId = deviceId;
            }

            ValidateTwinId(twin);

            if (string.IsNullOrEmpty(etag))
            {
                throw new ArgumentNullException(nameof(etag));
            }

            var errorMappingOverrides = new Dictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>>
            {
                {
                    HttpStatusCode.PreconditionFailed,
                    async responseMessage => new PreconditionFailedException(await ExceptionHandlingHelper.GetExceptionMessageAsync(responseMessage).ConfigureAwait(false))
                },
                {
                    HttpStatusCode.NotFound,
                    async responseMessage => new DeviceNotFoundException(await ExceptionHandlingHelper.GetExceptionMessageAsync(responseMessage).ConfigureAwait(false), (Exception)null)
                }
            };

            if (isReplace)
            {
                return this.httpClientHelper.PutAsync<Twin, Twin>(
                    GetTwinUri(deviceId),
                    twin,
                    etag,
                    etag == WildcardEtag ? PutOperationType.ForceUpdateEntity : PutOperationType.UpdateEntity,
                    errorMappingOverrides,
                    cancellationToken);
            }
            else
            {
                return this.httpClientHelper.PatchAsync<Twin, Twin>(
                    GetTwinUri(deviceId),
                    twin,
                    etag,
                    etag == WildcardEtag ? PutOperationType.ForceUpdateEntity : PutOperationType.UpdateEntity,
                    errorMappingOverrides,
                    cancellationToken);
            }
        }

        public override Task<BulkRegistryOperationResult> UpdateTwins2Async(IEnumerable<Twin> twins)
        {
            return this.UpdateTwins2Async(twins, false, CancellationToken.None);
        }

        public override Task<BulkRegistryOperationResult> UpdateTwins2Async(IEnumerable<Twin> twins, CancellationToken cancellationToken)
        {
            return this.UpdateTwins2Async(twins, false, cancellationToken);
        }

        public override Task<BulkRegistryOperationResult> UpdateTwins2Async(IEnumerable<Twin> twins, bool forceUpdate)
        {
            return this.UpdateTwins2Async(twins, forceUpdate, CancellationToken.None);
        }

        public override Task<BulkRegistryOperationResult> UpdateTwins2Async(IEnumerable<Twin> twins, bool forceUpdate, CancellationToken cancellationToken)
        {
            return this.BulkDeviceOperationsAsync<BulkRegistryOperationResult>(
                GenerateExportImportDeviceListForTwinBulkOperations(twins, forceUpdate ? ImportMode.UpdateTwin : ImportMode.UpdateTwinIfMatchETag),
                ClientApiVersionHelper.ApiVersionQueryString,
                cancellationToken);
        }

        Task RemoveDeviceAsync(string deviceId, IETagHolder eTagHolder, CancellationToken cancellationToken)
        {
            var errorMappingOverrides = new Dictionary<HttpStatusCode, Func<HttpResponseMessage, Task<Exception>>>
            {
                { HttpStatusCode.NotFound, async responseMessage =>
                                           {
                                               string responseContent = await ExceptionHandlingHelper.GetExceptionMessageAsync(responseMessage).ConfigureAwait(false);
                                               return new DeviceNotFoundException(responseContent, (Exception) null);
                                           }
                },
                { HttpStatusCode.PreconditionFailed, async responseMessage => new PreconditionFailedException(await ExceptionHandlingHelper.GetExceptionMessageAsync(responseMessage).ConfigureAwait(false)) }
            };

            return this.httpClientHelper.DeleteAsync(GetRequestUri(deviceId), eTagHolder, errorMappingOverrides, null, cancellationToken);
        }

        static Uri GetRequestUri(string deviceId)
        {
            deviceId = WebUtility.UrlEncode(deviceId);
            return new Uri(RequestUriFormat.FormatInvariant(deviceId, ClientApiVersionHelper.ApiVersionQueryString), UriKind.Relative);
        }

        static Uri GetBulkRequestUri(string apiVersionQueryString)
        {
            return new Uri(RequestUriFormat.FormatInvariant(string.Empty, apiVersionQueryString), UriKind.Relative);
        }

        static Uri GetJobUri(string jobId)
        {
            return new Uri(JobsUriFormat.FormatInvariant(jobId, ClientApiVersionHelper.ApiVersionQueryString), UriKind.Relative);
        }

        static Uri GetDevicesRequestUri(int maxCount)
        {
            return new Uri(DevicesRequestUriFormat.FormatInvariant(maxCount, ClientApiVersionHelper.ApiVersionQueryString), UriKind.Relative);
        }

        static Uri QueryDevicesRequestUri()
        {
            return new Uri(DevicesQueryUriFormat, UriKind.Relative);
        }

        static Uri GetAdminUri(string operation)
        {
            return new Uri(AdminUriFormat.FormatInvariant(operation, ClientApiVersionHelper.ApiVersionQueryString), UriKind.Relative);
        }

        static Uri GetStatisticsUri()
        {
            return new Uri(StatisticsUriFormat, UriKind.Relative);
        }

        private static Uri GetTwinUri(string deviceId)
        {
            deviceId = WebUtility.UrlEncode(deviceId);
            return new Uri(TwinUriFormat.FormatInvariant(deviceId, ClientApiVersionHelper.ApiVersionQueryString), UriKind.Relative);
        }

        private static Uri GetTwinTagsUri(string deviceId)
        {
            deviceId = WebUtility.UrlEncode(deviceId);
            return new Uri(TwinTagsUriFormat.FormatInvariant(deviceId, ClientApiVersionHelper.ApiVersionQueryString), UriKind.Relative);
        }

        private static Uri GetTwinDesiredPropertiesUri(string deviceId)
        {
            deviceId = WebUtility.UrlEncode(deviceId);
            return new Uri(TwinDesiredPropertiesUriFormat.FormatInvariant(deviceId, ClientApiVersionHelper.ApiVersionQueryString), UriKind.Relative);
        }

        static void ValidateDeviceId(Device device)
        {
            if (device == null)
            {
                throw new ArgumentNullException(nameof(device));
            }

            if (string.IsNullOrWhiteSpace(device.Id))
            {
                throw new ArgumentException("device.Id");
            }

            if (!DeviceIdRegex.IsMatch(device.Id))
            {
                throw new ArgumentException(ApiResources.DeviceIdInvalid.FormatInvariant(device.Id));
            }
        }

        static void ValidateTwinId(Twin twin)
        {
            if (twin == null)
            {
                throw new ArgumentNullException(nameof(twin));
            }

            if (string.IsNullOrWhiteSpace(twin.DeviceId))
            {
                throw new ArgumentException("twin.DeviceId");
            }

            if (!DeviceIdRegex.IsMatch(twin.DeviceId))
            {
                throw new ArgumentException(ApiResources.DeviceIdInvalid.FormatInvariant(twin.DeviceId));
            }
        }

        static void ValidateDeviceAuthentication(AuthenticationMechanism authentication, string deviceId)
        {
            if (authentication != null)
            {
                // Both symmetric keys and X.509 cert thumbprints cannot be specified for the same device
                bool symmetricKeyIsSet = !authentication.SymmetricKey?.IsEmpty() ?? false;
                bool x509ThumbprintIsSet = !authentication.X509Thumbprint?.IsEmpty() ?? false;

                if (symmetricKeyIsSet && x509ThumbprintIsSet)
                {
                    throw new ArgumentException(ApiResources.DeviceAuthenticationInvalid.FormatInvariant(deviceId ?? string.Empty));
                }

                // Validate X.509 thumbprints or SymmetricKeys since we should not have both at the same time
                if (x509ThumbprintIsSet)
                {
                    authentication.X509Thumbprint.IsValid(true);
                }
                else if (symmetricKeyIsSet)
                {
                    authentication.SymmetricKey.IsValid(true);
                }
            }
        }

        void EnsureInstanceNotClosed()
        {
            if (this.httpClientHelper == null)
            {
                throw new ObjectDisposedException("RegistryManager", ApiResources.RegistryManagerInstanceAlreadyClosed);
            }
        }

        async Task<QueryResult> ExecuteQueryAsync(string sqlQueryString, int? pageSize, string continuationToken, CancellationToken cancellationToken)
        {
            this.EnsureInstanceNotClosed();

            if (string.IsNullOrWhiteSpace(sqlQueryString))
            {
                throw new ArgumentException(IotHubApiResources.GetString(ApiResources.ParameterCannotBeNullOrEmpty, nameof(sqlQueryString)));
            }

            var customHeaders = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(continuationToken))
            {
                customHeaders.Add(ContinuationTokenHeader, continuationToken);
            }

            if (pageSize != null)
            {
                customHeaders.Add(PageSizeHeader, pageSize.ToString());
            }

            HttpResponseMessage response = await httpClientHelper.PostAsync(
                QueryDevicesRequestUri(),
                new QuerySpecification()
                {
                    Sql = sqlQueryString
                },
                null,
                customHeaders,
                new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" },
                null,
                cancellationToken).ConfigureAwait(false);

            return await QueryResult.FromHttpResponseAsync(response).ConfigureAwait(false);
        }

        static void NormalizeExportImportDevice(ExportImportDevice device)
        {
            // auto generate keys if not specified
            if (device.Authentication == null)
            {
                device.Authentication = new AuthenticationMechanism();
            }

            NormalizeAuthenticationInfo(device.Authentication);
        }

        static void NormalizeDevice(Device device)
        {
            // auto generate keys if not specified
            if (device.Authentication == null)
            {
                device.Authentication = new AuthenticationMechanism();
            }

            NormalizeAuthenticationInfo(device.Authentication);
        }

        static void NormalizeAuthenticationInfo(AuthenticationMechanism authenticationInfo)
        {
            //to make it backward compatible we set the type according to the values
            //we don't set CA type - that has to be explicit
            if (authenticationInfo.SymmetricKey != null && !authenticationInfo.SymmetricKey.IsEmpty())
            {
                authenticationInfo.Type = AuthenticationType.Sas;
            }

            if (authenticationInfo.X509Thumbprint != null && !authenticationInfo.X509Thumbprint.IsEmpty())
            {
                authenticationInfo.Type = AuthenticationType.SelfSigned;
            }
        }
    }
}
