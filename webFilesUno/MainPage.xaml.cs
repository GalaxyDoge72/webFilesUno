using System.ComponentModel.DataAnnotations;
using System.Dynamic;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.UI.Popups;
using System.Collections.ObjectModel;
using System.Diagnostics;
using SharpCompress.Archives;

namespace webFilesUno;

public sealed partial class MainPage : Page
{
    private static readonly HttpClient _client = new HttpClient(
        new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        }
    );

    private SemaphoreSlim _semaphore;
    private string BASE_URL = "https://kemono.cr";
    private const string API_VERSION = "api/v1";

    public ObservableCollection<DownloadItemViewModel> ActiveDownloads {get;} = new();

    public MainPage()
    {
        this.InitializeComponent();
        _client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        _client.DefaultRequestHeaders.Add("Accept", "text/css");

        flowLayoutPanelDownloads.ItemsSource = ActiveDownloads;
    }

    #region UI Controllers

    private void NavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var tag = args.InvokedItemContainer.Tag.ToString();

        DownloadSection.Visibility = (tag == "download") ? Visibility.Visible : Visibility.Collapsed;
        ExtractionSection.Visibility = (tag == "extraction") ? Visibility.Visible : Visibility.Collapsed;
        DeDupeSection.Visibility = (tag == "dedupe") ? Visibility.Visible : Visibility.Collapsed;

        flowLayoutPanelDownloads.Visibility = (tag == "download") ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion

    #region Path Helpers

    private static string sanitizeAndCap(string name, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(name)) return "post";

        string invalid = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        string sanitized = Regex.Replace(name, $"([{invalid}]|[\\. ]+$)", "_");

        if (sanitized.Length > maxLen)
        {
            sanitized = sanitized.Substring(0, maxLen).TrimEnd('_');
        }
        return sanitized;
    }

    #endregion

    #region Download Tab

    private void updateStatusLabel(string msg)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            StatusInfoBar.Message = msg;
        });
    }

    private string FormatBytesPerSecond(double bytes)
    {
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        int unitIndex = 0;
        while (bytes >= 1024 && unitIndex < units.Length - 1)
        {
            bytes /= 1024;
            unitIndex++;
        }
        return $"{bytes:F2} {units[unitIndex]}";
    }

    private async void startDownload_Click(object sender, RoutedEventArgs e)
    {
        string url = kemonoLinkTextBox.Text.Trim();
        BASE_URL = (BaseUrlComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "https://kemono.cr";
        string customFolderName = folderNameTextBox.Text.Trim();
        _semaphore = new SemaphoreSlim((int)maxDownloadsBar.Value);


        if (string.IsNullOrEmpty(url))
        {
            ContentDialog alert = new ContentDialog
            {
                Title = "Empty URL!",
                Content = "Please enter a kemono or coomer link.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await alert.ShowAsync();
        }

        Match match = BaseUrlComboBox.SelectedItem.ToString() switch
        {
            "https://kemono.cr" => Regex.Match(url, @"kemono\.cr/([^/]+)/(?:user|creator)/([^/\?]+)", RegexOptions.IgnoreCase),
            "https://coomer.st" => Regex.Match(url, @"kemono\.cr/([^/]+)/(?:user|creator)/([^/\?]+)", RegexOptions.IgnoreCase),
            _ => Regex.Match(url, @"kemono\.cr/([^/]+)/(?:user|creator)/([^/\?]+)", RegexOptions.IgnoreCase),
        };

        if (!match.Success)
        {
            ContentDialog alert = new ContentDialog
            {
                Title = "Invalid URL!",
                Content = "Please enter a valid URL format.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await alert.ShowAsync();
        }

        string service = match.Groups[1].Value;
        string userId = match.Groups[2].Value;

        string rootPath = (Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            string.IsNullOrWhiteSpace(customFolderName) ? "Kemono_Downloads" : customFolderName
        ));

        string creatorDir = Path.Combine(rootPath, $"{service}_{userId}");
        Directory.CreateDirectory(creatorDir);
        StartDownloadButton.IsEnabled = false;
        updateStatusLabel("Fetching Posts...");

        try
        {
            await fetchAndProcessPosts(service, userId, creatorDir);
        }
        catch (OperationCanceledException)
        {
            updateStatusLabel("Download cancelled by user.");
        }
        catch (Exception ex)
        {
            updateStatusLabel($"Error: {ex.Message}");
        }
        finally
        {
            StartDownloadButton.IsEnabled = true;
        }
    }
    
    private bool shouldDownload(string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant().TrimStart('.');
        return ext switch
        {
            // Add "?? false" to every CheckBox reference
            "jpg" or "jpeg" => jpegCheckBox.IsChecked ?? false,
            "png" => pngCheckBox.IsChecked ?? false,
            "mp4" => mpegCheckBox.IsChecked ?? false,
            "mov" => movCheckBox.IsChecked ?? false,
            "webm" => webmCheckBox.IsChecked ?? false,
            "gif" => gifCheckBox.IsChecked ?? false,
            "psd" => psdCheckBox.IsChecked ?? false,
            "zip" => zipCheckBox.IsChecked ?? false,
            "rar" => rarCheckBox.IsChecked ?? false,
            "7z" => sevenZipCheckBox.IsChecked ?? false,
            "mp3" => mpegAudioCheckBox.IsChecked ?? false,
            "wav" => wavCheckBox.IsChecked ?? false,
            "m4a" => mFourACheckBox.IsChecked ?? false,
            "flac" => flacCheckBox.IsChecked ?? false,
            "ogg" => oggCheckBox.IsChecked ?? false,
            _ => NoThumbnailCheckBox.IsChecked ?? false, // Re-using this because it doesn't work usually.
        };
    }

    private async Task fetchAndProcessPosts(string service, string userId, string creatorDir)
    {
        int offset = 0;
        bool hasMore = true;
        int processedCount = 0;
        
        // In many Kemono-like APIs, you might need a separate call to get the total, 
        // but usually, we can track progress relative to the current batch.
        
        while (hasMore)
        {
            string url = $"{BASE_URL}/{API_VERSION}/{service}/user/{userId}/posts?o={offset}";
            using var response = await _client.GetAsync(url);
            if (!response.IsSuccessStatusCode) break;

            string rawBody = await response.Content.ReadAsStringAsync();
            var posts = JsonSerializer.Deserialize<List<KemonoPost>>(rawBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (posts == null || posts.Count == 0) break;

            // Process each post one by one (or in parallel) and update the label
            foreach (var post in posts)
            {
                processedCount++;
                
                // Update the InfoBar at the bottom
                // We use the offset + current count to show progress
                updateStatusLabel($"Processing post {processedCount} (Page offset: {offset})...");
                
                await QueuePostDownloads(post, creatorDir);
            }
            
            if (posts.Count < 50) hasMore = false;
            else
            {
                offset += 50;
                await Task.Delay(500); // Respectful delay between API pages
            }
        }
        
        updateStatusLabel($"Finished! Fetched {processedCount} posts.");
    }
    int totalDownloadCount = 0;
    int downloadedCount = 0;
    private async Task QueuePostDownloads(KemonoPost post, string creatorDir)
    {
        // We cap the title strictly. ID is added to ensure uniqueness even if titles are truncated.
        string shortTitle = sanitizeAndCap(post.title, 50);
        string folderName = $"{shortTitle}_{post.id}";

        // Combine with the creator directory
        string postDir = Path.Combine(creatorDir, folderName);

        // Convert to long path format for safety
        string longPostDir = (postDir);

        var filesToDownload = new List<(string url, string name)>();

        // Process Main File
        if (post.file?.path != null)
        {
            string rawName = post.file.name ?? Path.GetFileName(post.file.path);
            // Cap the filename itself to 100 chars to be safe
            string safeFileName = sanitizeAndCap(Path.GetFileNameWithoutExtension(rawName), 100)
                                    + Path.GetExtension(rawName);

            if (shouldDownload(safeFileName))
                filesToDownload.Add((BASE_URL + post.file.path, safeFileName));
        }

            // Process Attachments
        if (post.attachments != null)
        {
            for (int i = 0; i < post.attachments.Count; i++)
            {
                var att = post.attachments[i];
                if (att.path != null)
                {
                    string rawName = att.name ?? Path.GetFileName(att.path);
                    string safeFileName = $"{i + 1}_" + sanitizeAndCap(Path.GetFileNameWithoutExtension(rawName), 100)
                                            + Path.GetExtension(rawName);

                    if (shouldDownload(safeFileName))
                        filesToDownload.Add((BASE_URL + att.path, safeFileName));
                }
            }
        }

        if (filesToDownload.Any())
        {
            Directory.CreateDirectory(longPostDir);
            foreach (var (url, name) in filesToDownload)
            {
                string destinationPath = Path.Combine(longPostDir, name);
                totalDownloadCount++;
                _ = downloadFileWithBarAsync(url, destinationPath, name);
            }
        }
    }

    private async Task downloadFileWithBarAsync(string url, string destinationPath, string fileName)
    {
        try { await _semaphore.WaitAsync(); }
        catch (Exception) { return; }
        
        

        // Create the ViewModel and add to UI via the collection
        var downloadVm = new DownloadItemViewModel { FileName = fileName, Percent = 0, StatusText = "Starting..." };
        
        // UI updates must happen on the Dispatcher thread
        this.DispatcherQueue.TryEnqueue(() => ActiveDownloads.Add(downloadVm));

        try
        {
            StartDownloadButton.IsEnabled = false;
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);

            byte[] buffer = new byte[8192];
            long readTotal = 0;
            int read;
            var sw = Stopwatch.StartNew();
            long lastUpdateTick = 0;

            

            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                readTotal += read;

                // Update UI every 250ms to prevent lag
                if (sw.ElapsedMilliseconds - lastUpdateTick > 150)
                {
                    lastUpdateTick = sw.ElapsedMilliseconds;
                    double speed = readTotal / sw.Elapsed.TotalSeconds;
                    int percent = totalBytes > 0 ? (int)(readTotal * 100 / totalBytes) : 0;

                    this.DispatcherQueue.TryEnqueue(() => {
                        downloadVm.Percent = percent;
                        downloadVm.StatusText = $"{percent}% - {FormatBytesPerSecond(speed)}";
                    });
                }
            }
            downloadedCount++;
            updateStatusLabel($"Downloaded {downloadedCount} of {totalDownloadCount} attachments.");

        }
        catch (Exception ex) { Debug.WriteLine($"Download error: {ex.Message}"); }
        finally
        {
            // Remove from UI collection when finished
            this.DispatcherQueue.TryEnqueue(() => ActiveDownloads.Remove(downloadVm));
            _semaphore.Release();
            StartDownloadButton.IsEnabled = true;
        }
    }

    #endregion

    #region Extraction Tab Logic

    private async void scanButton_Click(object sender, RoutedEventArgs e)
    {
        // Uno/WinUI uses FolderPicker instead of FolderBrowserDialog
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");

        if (App.MainWindow != null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            string selectedPath = folder.Path;
            string[] extensions = { ".zip", ".rar", ".7z", ".tar" };

            archiveFilesListView.Items.Clear();
            scanButton.IsEnabled = false;
            extractButton.IsEnabled = false;
            ExtractionStatusInfoBar.Message = "Scanning directory...";

            try
            {
                // Ported scanning logic from Form1.cs
                var foundFiles = await Task.Run(() =>
                {
                    var results = new List<extractionFileEntry>();
                    var files = Directory.EnumerateFiles(selectedPath, "*.*", SearchOption.AllDirectories)
                        .Where(file => extensions.Contains(Path.GetExtension(file).ToLower()));

                    foreach (var filePath in files)
                    {
                        results.Add(new extractionFileEntry
                        {
                            fileName = Path.GetFileName(filePath),
                            path = filePath
                        });
                    }
                    return results;
                });

                foreach (var entry in foundFiles)
                {
                    archiveFilesListView.Items.Add(entry);
                }
                ExtractionStatusInfoBar.Message = $"Found {foundFiles.Count} archives.";
            }
            catch (Exception ex)
            {
                ExtractionStatusInfoBar.Message = $"Scan error: {ex.Message}";
                ExtractionStatusInfoBar.Severity = InfoBarSeverity.Error;
            }
            finally
            {
                scanButton.IsEnabled = true;
                extractButton.IsEnabled = true;
            }
        }
    }

    private async void extractButton_Click(object sender, RoutedEventArgs e)
    {
        extractButton.IsEnabled = false;
        scanButton.IsEnabled = false;

        // Get items from the ListView
        var entries = archiveFilesListView.Items.Cast<extractionFileEntry>().ToList();
        bool overwrite = extractOverwriteCheckBox.IsChecked ?? true;
        int archiveCount = entries.Count;
        int currentCount = 0;

        await Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                try
                {
                    string currentFolder = Path.GetDirectoryName(entry.path);
                    // Using your SanitizeAndCap helper from Form1.cs
                    string folderName = sanitizeAndCap(Path.GetFileNameWithoutExtension(entry.path), 50);
                    string destinationPath = Path.Combine(currentFolder, folderName);

                    if (!Directory.Exists(destinationPath)) Directory.CreateDirectory(destinationPath);

                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        ExtractionStatusInfoBar.Title = "Extracting...";
                        ExtractionStatusInfoBar.Message = $"Processing: {entry.fileName} ({currentCount + 1}/{archiveCount})";
                    });

                    // Extraction logic using SharpCompress
                    using (var archive = ArchiveFactory.Open(entry.path))
                    {
                        foreach (var archiveEntry in archive.Entries.Where(x => !x.IsDirectory))
                        {
                            archiveEntry.WriteToDirectory(destinationPath, new SharpCompress.Common.ExtractionOptions()
                            {
                                ExtractFullPath = true,
                                Overwrite = overwrite
                            });
                        }
                    }
                    
                    // Cleanup: Delete archive after successful extraction
                    File.Delete(entry.path);
                    currentCount++;
                }
                catch (Exception ex)
                {
                    this.DispatcherQueue.TryEnqueue(() => 
                        Debug.WriteLine($"Error processing {entry.fileName}: {ex.Message}"));
                }
            }
        });

        archiveFilesListView.Items.Clear();
        extractButton.IsEnabled = true;
        scanButton.IsEnabled = true;
        ExtractionStatusInfoBar.Title = "Done";
        ExtractionStatusInfoBar.Message = "Extraction and cleanup complete!";
        ExtractionStatusInfoBar.Severity = InfoBarSeverity.Success;
    }

    #endregion

    #region De-Duplication Tab

    private void hashTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (hashTypeComboBox.SelectedItem is ComboBoxItem item)
        {
            string selected = item.Content.ToString();

            hashDescriptionTextBox.Text = selected switch
            {
                "MD5" => "Fastest hash, but capable of being duplicated.",
                "SHA-1" => "More expensive but fewer duplicates than MD5.",
                "SHA-256" => "The gold standard; very slow but no duplicates.",
                "XXHash64" => "Extremely fast, processing at RAM speeds.",
                _ => ""
            };
        }
    }

    private async void scanDupeButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        scanDupeButton.IsEnabled = false;
        string selectedAlgo = (hashTypeComboBox.SelectedItem as ComboBoxItem).Content.ToString();
        bool isDryRun = dryRunCheckBox.IsChecked ?? false;
        var greenBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
        var redBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
        await Task.Run(async () =>
        {
            var knownHashes = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            var files = Directory.GetFiles(folder.Path, "*.*", SearchOption.AllDirectories);
            int processed = 0;

            foreach (var filePath in files)
            {
                string hash = getFileHash(filePath, selectedAlgo);
                bool isDupe = !knownHashes.TryAdd(hash, filePath);

                

                
                if (isDupe && !isDryRun)
                {
                    try { File.Delete(filePath);} catch {}
                }
                else if (isDupe)
                {
                    var newItem = new DupeItemViewModel
                    {
                        FileName = Path.GetFileName(filePath),
                        Hash = hash,
                        Status = isDupe ? (isDryRun ? "[DUPE] Would Delete" : "[DUPE] Deleted") : "Unique",
                        StatusColor = isDupe ? redBrush : greenBrush
                    };

                    dupeListView.Items.Add(newItem);
                    dupeListView.ScrollIntoView(newItem);
                }
                processed++;
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    totalProgressBarDeDupe.Value = (processed * 100) / files.Length;
                    deDupeStatusLabel.Text = $"Processed {processed}/{files.Length}";
                });
            }
        });
    }

    private string getFileHash(string filePath, string algo)
    {
        if (algo == "XXHash64")
        {
            var hasher = new System.IO.Hashing.XxHash64();
            using var fs = File.OpenRead(filePath);
            hasher.Append(fs);
            return BitConverter.ToString(hasher.GetCurrentHash()).Replace("-", "").ToLower();
        }

        using var crypto = algo switch
        {
            "SHA-256" => (System.Security.Cryptography.HashAlgorithm)System.Security.Cryptography.SHA256.Create(),
            "SHA-1" => System.Security.Cryptography.SHA1.Create(),
            _ => System.Security.Cryptography.MD5.Create()
        };

        using var stream = File.OpenRead(filePath);
        return BitConverter.ToString(crypto.ComputeHash(stream)).Replace("-", "").ToLower();
    }

    #endregion
}

#region Models
public class extractionFileEntry
{
    public string fileName {get; set;}
    public string path {get; set;}
    public override string ToString() => fileName;
}

public class KemonoFile
{
    public string path {get; set;}
    public string name {get; set;}
}

public class KemonoPost
{
    public string id {get; set;}
    public string title {get; set;} 
    public KemonoFile file {get; set;} = new KemonoFile();
    public List<KemonoFile> attachments {get; set;} = new List<KemonoFile>();
}

public class DupeItemViewModel : System.ComponentModel.INotifyPropertyChanged
{
    public string FileName { get; set; }
    public string Hash { get; set; }
    public string Status { get; set; }
    
    // This determines the color in the UI
    public Microsoft.UI.Xaml.Media.SolidColorBrush StatusColor { get; set; }

    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
}
#endregion