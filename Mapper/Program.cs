using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.EventHubs;
using System.Configuration;

namespace Mapper2
{
    class Program
    {
        private static EventHubClient eventHubClient;
        private static PartitionSender sender0;
        private static PartitionSender sender1;
        private const string EventHubConnectionString = "";
        private const string EventHubName = "mapper-reducer-try3";


        static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }


        private static async Task MainAsync(string[] args)
        {
            // Creates an EventHubsConnectionStringBuilder object from the connection string, and sets the EntityPath.
            // Typically, the connection string should have the entity path in it, but for the sake of this simple scenario
            // we are using the connection string from the namespace.
            var connectionStringBuilder = new EventHubsConnectionStringBuilder(EventHubConnectionString)
            {
                EntityPath = EventHubName
            };

            eventHubClient = EventHubClient.CreateFromConnectionString(connectionStringBuilder.ToString());

            sender0 = eventHubClient.CreatePartitionSender("0");
            sender1 = eventHubClient.CreatePartitionSender("1");

        

            await SendMessagesToEventHub();

            await eventHubClient.CloseAsync();

            Console.WriteLine("Press ENTER to exit.");
            //Console.ReadLine();
        }

        // Creates an event hub client and sends 100 messages to the event hub.
        private static async Task SendMessagesToEventHub()
        {
       
            Console.WriteLine("Sending events now...");

            string[] letters = { "a", "b", "c", "d", "e", "f" };

            Random rand = new Random();

            int i;

            while (true)
            {
                i = rand.Next(letters.Length);
                Console.WriteLine($"Sending message: {letters[i]}");

                try
                {
                    //throw new Exception("test");

                    if (i % 2 == 0)
                    {
                        await sender0.SendAsync(new EventData(Encoding.UTF8.GetBytes(letters[i])));
                    }
                    else
                    {
                        await sender1.SendAsync(new EventData(Encoding.UTF8.GetBytes(letters[i])));
                    }

                }
                catch (Exception exception)
                {
                    Console.WriteLine($"{DateTime.Now} > Exception: {exception.Message}");
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            Console.WriteLine("outside of while loop");

        }

    }
}
