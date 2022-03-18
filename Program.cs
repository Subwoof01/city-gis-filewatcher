

namespace Scraper
{
    public class Program
    {
        private static volatile bool KeepRunning = true;
        
        public static void Main(string[] args)
        {
            ManualResetEvent exitEvent = new ManualResetEvent(false);

            Console.CancelKeyPress += (sender, eventArgs) => 
            {
                eventArgs.Cancel = true;
                exitEvent.Set();
            };
            
            bool readAll = false;
            Console.WriteLine("Read all files present in directory? (y/n)");
            if (Console.ReadKey(true).Key.Equals(ConsoleKey.Y))
                readAll = true;

            int logLevel = 1;
            Console.WriteLine("Set logging level (higher numbers include their predescessors):\n    0. No logging.\n    1. (DEFAULT) Essential logging only.\n    2. Simple logging of records pushed.\n    3. Detailed logging of records pushed.");
            ConsoleKey key = Console.ReadKey(true).Key;
            if (key.Equals(ConsoleKey.D0))
                logLevel = 0;
            else if (key.Equals(ConsoleKey.D2))
                logLevel = 2;
            else if (key.Equals(ConsoleKey.D3))
                logLevel = 3;
            
            Console.Clear();
            
            FileWatcher watcher = new FileWatcher(readAll, logLevel);

            exitEvent.WaitOne();

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[INFO] Scraper terminated by user (CTRL+C).");
            Environment.Exit(0);
        }
    }
}
