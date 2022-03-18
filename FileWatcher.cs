
using System.Globalization;
using CityGis.Data.RecorderReader;
using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using static System.Text.Json.JsonSerializer;
using File = System.IO.File;

namespace Scraper;

public class FileWatcher
{
    private const int ERROR_NO_CONNECTION = 0x194;
    private const int ERROR_LOADING_CONFIG = 0x6b;
    
    private FileSystemWatcher _watcher;
    private List<FileInfo> _files;

    private Configurations? _config;
    
    private bool _isLastFileChanged = false;
    private int _lastLineWrittenInFile = 0;
    private FileInfo? _lastFileWritten;
    private WriteApi _writeApi;
    private InfluxDBClient _client;
    private int _loggingLevel;

    public FileWatcher(bool firstRun = false, int loggingLevel = 3)
    {
        _files = new List<FileInfo>();
        
        if (_loggingLevel >= 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[INFO] Began scraper. Press CTRL+C to close.");
            Console.ForegroundColor = ConsoleColor.Gray;
            if (LoadConfig())
                Console.WriteLine($"[INFO] Loaded settings from config.");
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ERROR] Could not load config. Maybe some settings are wrong?");
                Console.WriteLine($"[ERROR] Process terminated with error code {ERROR_LOADING_CONFIG}");
                Environment.Exit(ERROR_LOADING_CONFIG);
            }
        }

        _client = InfluxDBClientFactory.Create($"http://{_config.Ip}:{_config.Port}", _config.Token);

        _writeApi = _client.GetWriteApi();
        _loggingLevel = loggingLevel;

        if (_loggingLevel >= 0)
        {
            Console.WriteLine($"[INFO] Watching directory: \"{_config.Directory}\"");
            Console.WriteLine($"[INFO] Reading all existing files: {firstRun.ToString()}");
        }

        _watcher = new FileSystemWatcher(_config.Directory);
        _watcher.EnableRaisingEvents = true;
        
        if (_loggingLevel >= 1)
            Console.WriteLine("[INFO] Created FileSystemWatcher.");

        _watcher.Created += OnFileCreated;
        _watcher.Changed += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        
        if (_loggingLevel >=1)
            Console.WriteLine("[INFO] Bound methods to events.");

        if (firstRun)
        {
            DirectoryInfo dir = new(_config.Directory);
            _files = dir.GetFiles().OrderBy(x => x.CreationTime).ToList();
        }
        
        if (_loggingLevel >= 1)
            Console.WriteLine("[INFO] Collected list of all files currently present.");

        // Make sure we actually have a connection to the database.
        if (!_client.PingAsync().Result)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] Could not connect to InfluxDB service at {_config.Ip}:{_config.Port}.");
            Console.WriteLine($"[ERROR] Process terminated with exit code {ERROR_NO_CONNECTION}");
            Console.ForegroundColor = ConsoleColor.Gray;
            Environment.Exit(ERROR_NO_CONNECTION);
        }


        if (firstRun)
        {
            ReadFilesInQueue();
            
            if (_loggingLevel >= 1)
                Console.WriteLine("[INFO] Finished initial read. Listening to changes in directory... ");
        }
    }

    private bool LoadConfig()
    {
        _config = Deserialize<Configurations>(File.ReadAllText(@"config\config.json"));
        if (_config == null)
            return false;
        return true;
    }

    private void ReadFilesInQueue(RecordingReader reader = null)
    {
        for (int i = _files.Count - 1; i >= 0; i--)
        {
            RecordingReader? input = (reader == null) ? RecordingReader.Open(_files[i].FullName) : reader;

            RecordingChange change;

            int count = 0;
            while (input.ReadRecord(out change))
            {
                // If we're still reading the same file, only push to db if we are reading new lines.
                if (_files[i] == _lastFileWritten && count < _lastLineWrittenInFile)
                    continue;

                if (change.ChangeType != RecordingChangeType.Remove)
                {
                    string record = $"{change.Record.Values.ElementAt(0)}";
                    string fields = $" ";
                    bool first = true;
                    for (int j = 1; j < change.Record.Keys.Count; j++)
                    {
                        if (change.Record.Values.ElementAt(j).Equals("") || change.Record.Values.ElementAt(j).Equals(0))
                            continue;

                        if (!first)
                            fields += ",";
                        
                        // Make sure to encase strings with "" so whitespaces do not mess with the API formatting.
                        bool isNumber = false;
                        int num;
                        if (int.TryParse(change.Record.Values.ElementAt(j).ToString(), out num))
                            isNumber = true;

                        string value = (isNumber) ? $"{change.Record.Values.ElementAt(j)}" : $"\"{change.Record.Values.ElementAt(j)}\"";
                        
                        fields += $"{change.Record.Keys.ElementAt(j)}={value}";

                        first = false;
                    }

                    record += fields;
                    record += $" {(UInt64)change.At.Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds}";
                    _writeApi.WriteRecord(record, WritePrecision.Ms, "data", "CityGIS");
                    
                    if (_loggingLevel >= 3)
                        Console.WriteLine($"[INFO] Pushed data: {record}");
                    else if (_loggingLevel >= 2)
                        Console.WriteLine($"[INFO] Pushed record with ID: {change.Record.Values.ElementAt(0)}.");
                }

                count++;
            }

            if (_loggingLevel >= 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[INFO] Finished reading file \"{_files[i].Name}\".");
                Console.ForegroundColor = ConsoleColor.Gray;
            }

            _lastLineWrittenInFile = count;
            _lastFileWritten = _files[i];
            _files.RemoveAt(i);
            Console.WriteLine(_files.Count);
        }
    }


    /// <summary>
    /// Makes sure the file is not still being written or copied etc.
    /// </summary>
    /// <param name="fullPath"></param>
    /// <returns></returns>
    private bool HasFileFinishedTask(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath))
                using (RecordingReader.Open(fullPath))
                {
                    RecordingReader? reader = RecordingReader.Open(fullPath);
                    ReadFilesInQueue(reader);
                    return true;
                }
            else
                return false;
        }
        catch (Exception)
        {
            Thread.Sleep(100);
            return HasFileFinishedTask(fullPath);
        }
    }
    

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_loggingLevel >= 1)
            Console.WriteLine($"[INFO] Detected change to file: {e.Name}.");
        _files.Add(new FileInfo(e.Name));
        HasFileFinishedTask(e.FullPath);
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (_loggingLevel >= 1)
            Console.WriteLine($"[INFO] Detected creation of file: {e.Name}.");
        _files.Add(new FileInfo(e.Name));
        HasFileFinishedTask(e.FullPath);
    }

    private void OnFileRenamed(object sender, FileSystemEventArgs e)
    {
        if (_loggingLevel >= 1)
            Console.WriteLine($"[INFO] Detected rename of file: {e.Name}.");
        _files.Add(new FileInfo(e.Name));
        if (HasFileFinishedTask(e.FullPath))
            ReadFilesInQueue();
    }
    
    
}