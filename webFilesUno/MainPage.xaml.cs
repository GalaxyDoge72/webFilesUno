using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.UI.Popups;

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

    public MainPage()
    {
        this.InitializeComponent();
        _client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        _client.DefaultRequestHeaders.Add("Accept", "text/css");
    }

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

    private async void startDownload_Click(object sender, RoutedEventArgs e)
    {
        string url = kemonoLinkTextBox.Text.Trim();
        string baseUrl = (BaseUrlComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "https://kemono.cr";
        string customFolderName = folderNameTextBox.Text.Trim();
        _semaphore = new SemaphoreSlim(5);


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
            "kemono.cr" => Regex.Match(url, @"kemono\.cr/([^/]+)/(?:user|creator)/([^/\?]+)", RegexOptions.IgnoreCase),
            "coomer.st" => Regex.Match(url, @"kemono\.cr/([^/]+)/(?:user|creator)/([^/\?]+)", RegexOptions.IgnoreCase),
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
            updateStatusLabel("Finished.");
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
        _ => false // Default for unknown extensions
    };
}

    private async Task fetchAndProcessPosts(string service, string userId, string creatorDir)
    {
        int offset = 0;
        bool hasMore = true;
        
        while (hasMore)
        {
            string url = $"{BASE_URL}/{API_VERSION}/{service}/user/{userId}/posts?o={offset}";
            using var response = await _client.GetAsync(url);
            if (!response.IsSuccessStatusCode) break;

            string rawBody = await response.Content.ReadAsStringAsync();
            var posts = JsonSerializer.Deserialize<List<KemonoPost>>(rawBody,
            new JsonSerializerOptions {PropertyNameCaseInsensitive = true});

            if (posts == null || posts.Count == 0) break;

            var postTasks = posts.Select(post => QueuePostDownloads(post, creatorDir));
            await Task.WhenAll(postTasks);
            
            if (posts.Count < 50) hasMore = false;
            else
            {
                offset += 50;
                await Task.Delay(500);
            }
        }
    }

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
            }
        }
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
#endregion