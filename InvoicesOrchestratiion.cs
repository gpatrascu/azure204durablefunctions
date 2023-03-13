using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Azurestudy
{

    public enum InvoiceStatus
    {
        Initiated = 0,
        Paid = 1,

        Cancelled = 2
    }
    public class Invoice
    {
        public Guid Id { get; set; }
        public string ClientName { get; set; }

        public string EmailAddress { get; set; }

        public string InvoiceAddress { get; set; }

        public decimal Value { get; set; }

        public InvoiceStatus Status { get; set; }

    }
    public static class InvoicesOrchestration
    {
        [FunctionName("SayHelloOrchestration")]
        public static async Task<List<string>> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var outputs = new List<string>();

            // Replace "hello" with the name of your Durable Activity Function.
            outputs.Add(await context.CallActivityAsync<string>(nameof(SayHello), "Tokyo"));
            outputs.Add(await context.CallActivityAsync<string>(nameof(SayHello), "Seattle"));
            outputs.Add(await context.CallActivityAsync<string>(nameof(SayHello), "London"));

            // returns ["Hello Tokyo!", "Hello Seattle!", "Hello London!"]
            return outputs;
        }

        [FunctionName(nameof(SayHello))]
        public static string SayHello([ActivityTrigger] string name, ILogger log)
        {
            log.LogInformation("Saying hello to {name}.", name);
            return $"Hello {name}!";
        }

        [FunctionName("SayHello_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {
            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("SayHelloOrchestration", null);

            log.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);

            return starter.CreateCheckStatusResponse(req, instanceId);
        }

        [FunctionName("InvoiceOrchestration_HttpStart")]
        public static async Task<HttpResponseMessage> StartInvoiceOrchestration(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {
            Invoice invoice = await req.Content.ReadAsAsync<Invoice>();
            invoice.Id = Guid.NewGuid();
            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("InvoiceOrchestration", invoice);

            log.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);

            return starter.CreateCheckStatusResponse(req, instanceId);
        }


        [FunctionName("InvoiceOrchestration")]
        public static async Task RunInvoiceOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var invoice = context.GetInput<Invoice>();
            // Replace "hello" with the name of your Durable Activity Function.
            await context.CallActivityAsync(nameof(AddInvoice), invoice);

            using (var timeoutCts = new CancellationTokenSource())
            {
                DateTime dueTime = context.CurrentUtcDateTime.AddMinutes(1);
                Task durableTimeout = context.CreateTimer(dueTime, timeoutCts.Token);

                Task<bool> paidEvent = context.WaitForExternalEvent<bool>("PayEvent");
                if (paidEvent == await Task.WhenAny(paidEvent, durableTimeout))
                {
                    timeoutCts.Cancel();
                    await context.CallActivityAsync("InvoicePaid", invoice);
                }
                else
                {
                    await context.CallActivityAsync("InvoiceCancelled", invoice);
                }
            }
        }

        [FunctionName("AddInvoice")]
        public static async Task AddInvoice(
            [ActivityTrigger] Invoice invoice,
            ILogger log,
             [Sql("dbo.InvoicesDurable", "SqlConnectionString")] IAsyncCollector<Invoice> invoices)
        {
            await invoices.AddAsync(invoice);
            await invoices.FlushAsync();
        }

        [FunctionName("InvoicePaid")]
        public static async Task InvoicePaid(
            [ActivityTrigger] Invoice invoice,
            ILogger log,
             [Sql("dbo.InvoicesDurable", "SqlConnectionString")] IAsyncCollector<Invoice> invoices)
        {
            invoice.Status = InvoiceStatus.Paid;
            await invoices.AddAsync(invoice);
            await invoices.FlushAsync();
        }

        [FunctionName("InvoiceCancelled")]
        public static async Task InvoiceCancelled(
            [ActivityTrigger] Invoice invoice,
            ILogger log,
             [Sql("dbo.InvoicesDurable", "SqlConnectionString")] IAsyncCollector<Invoice> invoices)
        {
            invoice.Status = InvoiceStatus.Cancelled;
            await invoices.AddAsync(invoice);
            await invoices.FlushAsync();
        }

        [FunctionName("RaisePayEvent")]
        public static async Task Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "invoices/event/{instanceId}")]
        HttpRequestMessage req,
        string instanceId,
        [DurableClient] IDurableOrchestrationClient client)
        {
            bool isPaid = true;
            await client.RaiseEventAsync(instanceId, "PayEvent", isPaid);
        }
    }
}