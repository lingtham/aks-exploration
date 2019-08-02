using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.EventHubs;
using System.Configuration;
using RocksDbSharp;
using System.IO;
using System.Threading;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Reducer
{
    class Program
    {
        
        private static string dir = "DiskFiles";
        private static string dbPath = "C:\\" + dir + "\\RocksDB\\lettersDB";
        private static string backupPath = "C:\\" + dir + "\\RocksDBCheckpoint";
        private static Logger log = new Logger(dir);
        
        private const string EventHubConnectionString = "Endpoint=sb://lithameh.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=si7lzvlr5n5pVm7eCsCnJa+xRX5FcDBS/daOj1lK8uw=";

        private static StringBuilder logMessage = new StringBuilder();
        private static StringBuilder resultsEvent = new StringBuilder();

        //variable that denotes master/slave role, if isMaster = true, then this service will send results to another Event Hub
        private static bool isMaster = false; //default state is not master

        private static string partitionId = Environment.GetEnvironmentVariable("partitionId");
        private static string replicaNum = Environment.GetEnvironmentVariable("replicaNum");

        static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        private static async Task MainAsync(string[] args)
        {
            

           
                string storageConnectionString = "DefaultEndpointsProtocol=https;AccountName=lithamstorage;AccountKey=Vd8egneKvbPDsqFIhGNl1+EiZXuot6QfSSOUyX7jYqSM0car16M/S2QQbrYdKPQ2YIJamohUnfil9DY2X9G0Yw==;EndpointSuffix=core.windows.net";
                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);
                CloudBlobClient cloudBlobClient = storageAccount.CreateCloudBlobClient();
                CloudBlobContainer container = cloudBlobClient.GetContainerReference($"rocksdb-backup-partition{partitionId}-replica{replicaNum}"); //make this ifnotexistscreate method
                
                /*
                 * Commented this out because container.CreateIfNotExistAsync was throwing a "the remote name could not be resolved" exception. Couldn't find out why
                 
                await container.CreateIfNotExistsAsync();

            int numRetries = 3;
            int retryDelay = 1000; //milliseconds
            for (int i = 0; i < numRetries; i++)
            {
                try
                {
                    Console.WriteLine("in try loop for container creation");
                    await container.CreateIfNotExistsAsync();
                    Console.WriteLine("container created succesfully or already exists");
                    break;
                }
                catch (Exception ex) when (i < numRetries)
                {
                    //Console.WriteLine($"{ex}")
                    await Task.Delay(retryDelay);

                }
            }
            */  
                
            try
            {
                if (!Directory.Exists(dbPath)) //in cases of disk failure on AKS or container restart locally on Docker
                {
                    Console.WriteLine("dbPath doesn't exist");
                    Directory.CreateDirectory(dbPath);

                    //default max of blobs returned in one call is 5000, uses continuation token to keep track of where you stopped
                    BlobContinuationToken blobContinuationToken = null;

                    do
                    {
                        var allBlobs = await container.ListBlobsSegmentedAsync(null, blobContinuationToken); //each call returns a max of 5000 blobs
                        blobContinuationToken = allBlobs.ContinuationToken;

                        foreach (var result in allBlobs.Results)
                        {
                            Console.WriteLine("in blob loop");
                            CloudBlockBlob blob = (CloudBlockBlob)result; //result is originally of type IBlobItem, have to cast to CloudBlockBlob to use fields and methods

                            await blob.DownloadToFileAsync(dbPath + "\\" + blob.Name, FileMode.Create);
                            await blob.DeleteIfExistsAsync(); //so that we won't reuse files from other instances of the app **this method doesn't include delete snapshots
                        }

                    } while (blobContinuationToken != null);

                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ex}");
                Console.ReadLine();
            }

            //Directory.CreateDirectory("C:\\" + dir + "\\RocksDB");
            var options = new DbOptions().SetCreateIfMissing(true);
            var db = RocksDb.Open(options, dbPath);

            var checkpoint = db.Checkpoint();
            Task makeBackup = DataBackupAndLog(checkpoint, container);



            //start task that receives events from Event Hub
            Task receiveEvents = receiveEventsFromEventHub(db);

            //start task that tries to get blob lease 
            Task blobLease = getLease();

            var connectionStringBuilder = new EventHubsConnectionStringBuilder(EventHubConnectionString)
            {
                EntityPath = $"results-store"
            };

            var resultsEventHubClient = EventHubClient.CreateFromConnectionString(connectionStringBuilder.ToString());

            while (true)
            {
                //Console.WriteLine($"{DateTime.Now}: while loop, isMaster: {isMaster}");
                if (isMaster)
                {
                    await sendResultsToEventHub(resultsEventHubClient, db);
                }

                await Task.Delay(TimeSpan.FromMinutes(1));
            }
            
        }

        private static async Task receiveEventsFromEventHub(RocksDb db)
        {
            //Create Event Hub Client
            var connectionStringBuilder = new EventHubsConnectionStringBuilder("Endpoint=sb://lithameh.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=si7lzvlr5n5pVm7eCsCnJa+xRX5FcDBS/daOj1lK8uw=")
            {
                EntityPath = "mapper-reducer-try3"
            };

            var eventHubClient = EventHubClient.CreateFromConnectionString(connectionStringBuilder.ToString());


            //Creates a receiver that starts receiving from the first available event in the specified partition (by PartitionId, can use environment variables or read 

            PartitionReceiver receiver = eventHubClient.CreateReceiver(PartitionReceiver.DefaultConsumerGroupName, partitionId, EventPosition.FromStart());

           

            //A dictionary that stores the letter as the key and # of that letter as value
            Dictionary<String, int> letterCount = new Dictionary<String, int>();
            int amt = 0;

            using (var iterator = db.NewIterator())
            {
                iterator.SeekToFirst();
                while (iterator.Valid())
                {
                    int.TryParse(iterator.StringValue(), out amt);
                    letterCount.Add(iterator.StringKey(), amt);
                    iterator.Next();
                }
            }

            

            //timer for logging count, Tick is the method that will happen every X minutes
            //Timer timer = new System.Threading.Timer(Tick, null, (int)TimeSpan.FromMinutes(1).TotalMilliseconds, (int)TimeSpan.FromMinutes(2).TotalMilliseconds);
            //Timer logTimer = new System.Threading.Timer(Tick, null, 1000, (int)TimeSpan.FromMinutes(2).TotalMilliseconds);



            while (true)
            {
                try
                {

                    // Receive a maximum of all available messages in this call to ReceiveAsync
                    IEnumerable<EventData> ehEvents = await receiver.ReceiveAsync(100);
                    // ReceiveAsync can return null if there are no messages
                    if (ehEvents != null)
                    {
                        Console.WriteLine("receiving messages");
                        // Since ReceiveAsync can return more than a single event you will need a loop to process
                        foreach (EventData ehEvent in ehEvents)
                        {
                            // Decode the byte array segment
                            var message = Encoding.UTF8.GetString(ehEvent.Body.Array);
                            //Console.WriteLine($"receiving message: {message}");

                            //processing logic here
                            if (!letterCount.ContainsKey(message))
                            {
                                letterCount.Add(message, 1);
                            }
                            else
                            {
                                letterCount[message]++;
                            }

                            await Task.Delay(TimeSpan.FromSeconds(1));

                        }

                        logMessage.Clear();

                        foreach (var pair in letterCount)
                        {
                            db.Put(pair.Key, (pair.Value).ToString());
                            Console.WriteLine($"{pair.Key} : {pair.Value}");
                            logMessage.AppendFormat("{0}: {1} ", pair.Key, db.Get(pair.Key));
                        }



                    }
                    else
                    {
                        Console.WriteLine("Received no messages. Press ENTER to stop worker.");

                        //Console.ReadLine();
                    }
                }

                catch (Exception e)
                {
                    Console.WriteLine($"Error: {e.Message}");
                    Console.ReadLine();
                    log.errorLog(e.Message);
                    //logTimer.Dispose();
                    break;
                }


            }

            await receiver.CloseAsync();
            await eventHubClient.CloseAsync();
        }
    
        /*
        //timer calls the method to happen every X minutes, this method is just a wrapper for a logging method
        private static void Tick(object state)
        {
            log.countLog(logMessage);
        }
        */

        private static async Task getLease()
        {
            Console.WriteLine($"{DateTime.Now}: in getLease()");
            //connection info for storage acct
            string storageConnectionString = "DefaultEndpointsProtocol=https;AccountName=lithamstorage;AccountKey=Vd8egneKvbPDsqFIhGNl1+EiZXuot6QfSSOUyX7jYqSM0car16M/S2QQbrYdKPQ2YIJamohUnfil9DY2X9G0Yw==;EndpointSuffix=core.windows.net";
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);

            int leaseLengthInSeconds = 60; //lease length can be between 15-60s or infinite (0s)

            //Create or obtain a reference to a blob called "lease.txt" in the container "locks"
            string leaseid = string.Empty;
            CloudBlobClient cloudBlobClient = storageAccount.CreateCloudBlobClient();
            CloudBlobContainer container = cloudBlobClient.GetContainerReference("letter-store");
            string blobName = $"results{partitionId}";
            CloudBlockBlob blob = container.GetBlockBlobReference(blobName);

            
            if (!blob.Exists())
            {
                blob.UploadFromStream(new MemoryStream());
            }
            

            while (true)
            {
                if (string.IsNullOrEmpty(leaseid)) //no lease so lets try to get one
                {
                    TimeSpan? leaseTime = TimeSpan.FromSeconds(leaseLengthInSeconds); //Acquire a lease on the blob. 
                    string proposedLeaseId = Guid.NewGuid().ToString(); //proposed lease id (leave it null for storage service to return you one).

                    try
                    {
                        leaseid = blob.AcquireLease(leaseTime, proposedLeaseId);

                        //Got a lease so this is the master role
                        isMaster = true;
                        log.leaseLog("lease started");
                        Console.WriteLine($"{DateTime.Now}: got lease - initial attempt");
                    }
                    catch (StorageException ex)
                    {
                        //Something happened and we were unable to obtain the lease
                        isMaster = false;
                        leaseid = string.Empty;


                        //For reference you can interrogate the response from Azure blob storage this way if you want more details
                        string err = ex.RequestInformation.ExtendedErrorInformation.ErrorCode;
                        Console.WriteLine($"in no lease: {err}");
                    }
                }

                else //lease already exists so let's try to renew
                {
                    try
                    {
                        //Specify the lease ID that we're trying to renew and attempt
                        AccessCondition ac = new AccessCondition();
                        ac.LeaseId = leaseid;
                        blob.RenewLease(ac);


                        //It must have worked or an exception would have been thrown so we're keeping the master role
                        isMaster = true;
                        log.leaseLog($"{DateTime.Now}: renewed lease");

                    }
                    catch (StorageException ex)
                    {
                        Console.WriteLine($"lease already exists: {ex}");
                        //Failure renewing -- relinquish master role -- again you could interrogate the error code here if you had other logic to apply
                        isMaster = false;
                        leaseid = string.Empty;
                        log.leaseLog("renew lease failed");

                    }

                }

               
                   
                await Task.Delay((leaseLengthInSeconds * 1000) - 14000);
                    /*
                    Sleep for lease duration minus 20 seconds
                    This renews the lease early for a lease length of 60s because when you call AcquireLease with a valid lease id, the lease will extend and start the clock over on the expiration
                    Other instances shouldn't be able to get the lease because it hasn't expired yet  
                    This should keep the same instance as the master unless it goes down
                    */
                

            }

        }

        private static async Task sendResultsToEventHub(EventHubClient ehClient, RocksDb db)
        {

            //Console.WriteLine($"{DateTime.Now}: in send events method");

            resultsEvent.Clear();

            resultsEvent.AppendFormat("partition {0}, replica {1} - ", partitionId, replicaNum);

            using (var iterator = db.NewIterator())
            {
                iterator.SeekToFirst();

                while (iterator.Valid())
                {

                    resultsEvent.AppendFormat("{0}: {1} ", iterator.StringKey(), iterator.StringValue());

                    iterator.Next();
                }
            }

            log.resultsLog(resultsEvent);
            await ehClient.SendAsync(new EventData(Encoding.UTF8.GetBytes(resultsEvent.ToString())));
        }

        private static async Task DataBackupAndLog(Checkpoint checkpoint, CloudBlobContainer container)
        {
            
            //upload checkpoint files to blob storage every X minutes
            while (true)
            {
                try
                {
                    log.countLog(logMessage); //logs count results from calculations in receiveEventsFromEventHub

                    if (Directory.Exists(backupPath))
                    {
                        Directory.Delete(backupPath, true); //the directory must NOT exist at time of Checkpoint.Save() method so delete if it exists
                        //the true parameter is necessary to recursively delete all the subdirectories and files in the directory
                    }

                    Console.WriteLine($"{DateTime.Now} start checkpoint");
                    checkpoint.Save(backupPath); //the directory must NOT exist at time of save
                    Console.WriteLine($"{DateTime.Now} end checkpoint");

                    string blobName;

                    var folder = new DirectoryInfo(backupPath);
                    var files = folder.GetFiles();

                    foreach (var file in files)
                    {
                        blobName = file.Name;
                        CloudBlockBlob blob = container.GetBlockBlobReference(blobName);
                        await blob.UploadFromFileAsync(backupPath + "\\" + file.Name);
                    }


                    await Task.Delay(TimeSpan.FromMinutes(5));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{ex}");
                    break;
                }
            }

        }
    }
}
