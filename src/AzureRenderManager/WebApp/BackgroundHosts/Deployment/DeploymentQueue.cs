﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using System;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Queue;
using Newtonsoft.Json;

namespace WebApp.BackgroundHosts.Deployment
{
    public class DeploymentQueue : IDeploymentQueue
    {
        private const string QueueName = "deployments";

        private readonly CloudQueueClient _queueClient;

        public DeploymentQueue(CloudQueueClient queueClient)
        {
            _queueClient = queueClient;
        }

        public async Task Add(ActiveDeployment activeDeployment)
        {
            var queue = _queueClient.GetQueueReference(QueueName);
            await queue.CreateIfNotExistsAsync();
            await queue.AddMessageAsync(
                new CloudQueueMessage(
                    JsonConvert.SerializeObject(activeDeployment)));
        }

        public async Task Delete(string messageId, string popReceipt)
        {
            try
            {
                var queue = _queueClient.GetQueueReference(QueueName);
                if (await queue.ExistsAsync())
                {
                    await queue.DeleteMessageAsync(messageId, popReceipt);
                }
            }
            catch (Exception e)
            {
                Console.Write(e);
            }

        }

        public async Task<ActiveDeployment> Get()
        {
            var queue = _queueClient.GetQueueReference(QueueName);
            if (await queue.ExistsAsync())
            {
                var cloudMessage = await queue.GetMessageAsync();
                if (cloudMessage != null)
                {
                    var activeDeployment = JsonConvert.DeserializeObject<ActiveDeployment>(cloudMessage.AsString);
                    activeDeployment.MessageId = cloudMessage.Id;
                    activeDeployment.PopReceipt = cloudMessage.PopReceipt;
                    activeDeployment.QueueName = QueueName;
                    return activeDeployment;
                }
            }
            return null;
        }
    }
}
