using System;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Text.Json;
using System.Diagnostics;
using System.Web;
using System.Net;
using System.Linq;
using System.Security.Cryptography;

namespace McWhitelistTrayApp
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApplicationContext());
        }
    }

    public class TrayApplicationContext : ApplicationContext
    {
        private NotifyIcon trayIcon;
        private HttpClient httpClient;
        private System.Threading.Timer pingTimer;
        private string tokenFilePath;
        private string deviceToken;
        private string sessionToken;
        private UserData currentUser;
        private HttpListener authListener;
        private int currentCallbackPort = 0;

        private const string APP_URL = "https://mcwhitelist.alokyn.com";
        private const string AUTHORIZE_URL = APP_URL + "/api/auth/desktop/authorize";
        private const string VERIFY_URL = APP_URL + "/api/auth/desktop/verify";
        private const string LOGOUT_URL = APP_URL + "/api/auth/desktop/logout";
        private const string PROFILE_URL = APP_URL + "/settings";
        private const int PING_INTERVAL_SECONDS = 5; 

        public class UserData
        {
            public string Id { get; set; }
            public string Username { get; set; }
            public string Email { get; set; }
            public string ShortId { get; set; }
        }

        public TrayApplicationContext()
        {
            tokenFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "McWhitelist",
                "device_token.txt"
            );

            Directory.CreateDirectory(Path.GetDirectoryName(tokenFilePath));
            
            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            
            sessionToken = GenerateSessionToken();
            Console.WriteLine($"Session Token Generated: {sessionToken}");
            
            trayIcon = new NotifyIcon()
            {
                Icon = CreateIcon(),
                ContextMenuStrip = CreateContextMenu(),
                Visible = true,
                Text = "McWhitelist"
            };

            _ = InitializeAsync();
        }

        private string GenerateSessionToken()
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                byte[] tokenData = new byte[32];
                rng.GetBytes(tokenData);
                string token = Convert.ToBase64String(tokenData)
                    .Replace("+", "")
                    .Replace("/", "")
                    .Replace("=", "");

                return token.Length >= 43 ? token.Substring(0, 43) : token;
            }
        }


        private async Task InitializeAsync()
        {
            LoadToken();
            
            if (IsLoggedIn())
            {
                var verified = await VerifyDeviceTokenWithRetry();
                if (verified)
                {
                    StartPingTimer();
                    UpdateTrayText();
                    RefreshMenu();
                    ShowNotification("McWhitelist", $"Logged in as {currentUser.Username}");
                }
                else
                {
                    DeleteToken();
                    RefreshMenu();
                }
            }
        }

        private Icon CreateIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                
                if (File.Exists(iconPath))
                {
                    return new Icon(iconPath, new Size(16, 16));
                }
                
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var resourceName = "McWhitelistTrayApp.icon.ico";
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        return new Icon(stream, new Size(16, 16));
                    }
                }
                
                return SystemIcons.Application;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load icon: {ex.Message}");
                return SystemIcons.Application;
            }
        }

        private ContextMenuStrip CreateContextMenu()
        {
            var menu = new ContextMenuStrip();
            
            if (IsLoggedIn() && currentUser != null)
            {
                var usernameItem = new ToolStripMenuItem($"👤 {currentUser.Username}");
                usernameItem.Enabled = false;
                usernameItem.Font = new Font(usernameItem.Font, FontStyle.Bold);
                menu.Items.Add(usernameItem);

                var idItem = new ToolStripMenuItem($"ID: {currentUser.ShortId}");
                idItem.Enabled = false;
                idItem.ForeColor = Color.Gray;
                menu.Items.Add(idItem);

                menu.Items.Add(new ToolStripSeparator());

                var profileItem = new ToolStripMenuItem("View Profile");
                profileItem.Click += ViewProfile_Click;
                menu.Items.Add(profileItem);

                menu.Items.Add(new ToolStripSeparator());

                var logoutItem = new ToolStripMenuItem("Logout");
                logoutItem.Click += LoginLogout_Click;
                menu.Items.Add(logoutItem);
            }
            else
            {
                var loginItem = new ToolStripMenuItem("Login");
                loginItem.Click += LoginLogout_Click;
                menu.Items.Add(loginItem);
            }

            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += Exit_Click;
            menu.Items.Add(exitItem);

            return menu;
        }

        private void RefreshMenu()
        {
            trayIcon.ContextMenuStrip?.Dispose();
            trayIcon.ContextMenuStrip = CreateContextMenu();
        }

        private void UpdateTrayText()
        {
            if (currentUser != null)
            {
                trayIcon.Text = $"McWhitelist - {currentUser.Username} ({currentUser.ShortId})";
            }
            else
            {
                trayIcon.Text = "McWhitelist - Not logged in";
            }
        }

        private bool IsLoggedIn()
        {
            return !string.IsNullOrEmpty(deviceToken);
        }

        private void LoadToken()
        {
            try
            {
                if (File.Exists(tokenFilePath))
                {
                    deviceToken = File.ReadAllText(tokenFilePath).Trim();
                }
            }
            catch (Exception ex)
            {
                ShowNotification("Error", $"Failed to load token: {ex.Message}");
            }
        }

        private void SaveToken(string token)
        {
            try
            {
                deviceToken = token;
                File.WriteAllText(tokenFilePath, token);
            }
            catch (Exception ex)
            {
                ShowNotification("Error", $"Failed to save token: {ex.Message}");
            }
        }

        private void DeleteToken()
        {
            try
            {
                deviceToken = null;
                currentUser = null;
                if (File.Exists(tokenFilePath))
                {
                    File.Delete(tokenFilePath);
                }
            }
            catch (Exception ex)
            {
                ShowNotification("Error", $"Failed to delete token: {ex.Message}");
            }
        }

        private async void LoginLogout_Click(object sender, EventArgs e)
        {
            if (IsLoggedIn())
            {
                await Logout();
            }
            else
            {
                await Login();
            }
        }

        private void ViewProfile_Click(object sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = PROFILE_URL,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowNotification("Error", $"Failed to open profile: {ex.Message}");
            }
        }

        private int FindAvailablePort(int startPort = 8787, int maxAttempts = 10)
        {
            for (int port = startPort; port < startPort + maxAttempts; port++)
            {
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://localhost:{port}/");
                    listener.Start();
                    listener.Stop();
                    return port;
                }
                catch
                {
                    continue;
                }
            }
            throw new Exception($"No available ports found between {startPort} and {startPort + maxAttempts}");
        }

        private async Task Login()
        {
            try
            {
                ShowNotification("Login", "Opening browser for authorization...");

                currentCallbackPort = FindAvailablePort();
                string callbackUri = $"http://localhost:{currentCallbackPort}/callback";

                authListener = new HttpListener();
                authListener.Prefixes.Add($"http://localhost:{currentCallbackPort}/");
                authListener.Start();

                string deviceName = Environment.MachineName;
                string deviceInfo = $"Windows {Environment.OSVersion.Version} - McWhitelist Desktop";

                var authUrl = $"{AUTHORIZE_URL}?redirect_uri={HttpUtility.UrlEncode(callbackUri)}&device_name={HttpUtility.UrlEncode(deviceName)}&device_info={HttpUtility.UrlEncode(deviceInfo)}";
                Process.Start(new ProcessStartInfo
                {
                    FileName = authUrl,
                    UseShellExecute = true
                });

                var contextTask = authListener.GetContextAsync();
                var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5));
                var completedTask = await Task.WhenAny(contextTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    authListener.Stop();
                    ShowNotification("Error", "Login timeout. Please try again.");
                    return;
                }

                var context = contextTask.Result;
                var query = context.Request.QueryString;

                var token = query.Get("token");
                var username = query.Get("username");
                var shortId = query.Get("shortId");

                var response = context.Response;
                string responseString = GetSuccessHtml(username, shortId);
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
                authListener.Stop();

                if (!string.IsNullOrEmpty(token))
                {
                    SaveToken(token);

                    currentUser = new UserData
                    {
                        Id = null,           
                        Username = username,
                        Email = null,
                        ShortId = shortId
                    };

                    sessionToken = GenerateSessionToken();
                    Console.WriteLine($"New Session Token on Login: {sessionToken}");

                    StartPingTimer();
                    RefreshMenu();
                    UpdateTrayText();
                    ShowNotification("Success", $"Logged in as {username}!");
                }

                else
                {
                    ShowNotification("Error", "Authorization failed. Please try again.");
                }
            }
            catch (Exception ex)
            {
                ShowNotification("Error", $"Login failed: {ex.Message}");
                authListener?.Stop();
            }
        }

        private async Task<bool> VerifyDeviceTokenWithRetry()
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var response = await httpClient.PostAsJsonAsync(VERIFY_URL, new 
                    { 
                        deviceToken,
                        sessionToken
                    });
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var jsonString = await response.Content.ReadAsStringAsync();
                        var document = JsonDocument.Parse(jsonString);
                        var root = document.RootElement;
                        var user = root.GetProperty("user");
                        
                        currentUser = new UserData
                        {
                            Id = user.GetProperty("id").ToString(),
                            Username = user.GetProperty("username").GetString(),
                            Email = user.GetProperty("email").GetString(),
                            ShortId = user.GetProperty("uniqueId").GetString()
                        };
                        
                        return true;
                    }
                    
                    if (response.StatusCode == HttpStatusCode.Unauthorized) 
                        return false;
                        
                    if (attempt < 3) 
                        await Task.Delay(2000 * attempt);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Verify attempt {attempt} failed: {ex.Message}");
                    if (attempt < 3) 
                        await Task.Delay(2000 * attempt);
                }
            }
            return false;
        }

        private async Task Logout()
        {
            try
            {
                if (!string.IsNullOrEmpty(deviceToken))
                {
                    await httpClient.PostAsJsonAsync(LOGOUT_URL, new
                    {
                        deviceToken = deviceToken
                    });
                }

                StopPingTimer();
                DeleteToken();
                RefreshMenu();
                UpdateTrayText();
                
                ShowNotification("Success", "Logged out successfully!");
            }
            catch (Exception ex)
            {
                ShowNotification("Error", $"Logout failed: {ex.Message}");
            }
        }

        private void StartPingTimer()
        {
            StopPingTimer();
            pingTimer = new System.Threading.Timer(
                async _ => await SendPing(),
                null,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(PING_INTERVAL_SECONDS)
            );
        }

        private void StopPingTimer()
        {
            pingTimer?.Dispose();
            pingTimer = null;
        }

        private async Task SendPing()
        {
            if (!IsLoggedIn()) return;

            try
            {
                var response = await httpClient.PostAsJsonAsync(VERIFY_URL, new
                {
                    deviceToken = deviceToken,
                    sessionToken = sessionToken
                });

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Ping sent successfully (Session: {sessionToken.Substring(0, 8)}...)");
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Ping failed: {response.StatusCode}");
                    
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        DeleteToken();
                        StopPingTimer();
                        RefreshMenu();
                        UpdateTrayText();
                        ShowNotification("Session Expired", "You have been logged out. Please log in again.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Ping error: {ex.Message}");
            }
        }

        private void ShowNotification(string title, string message)
        {
            trayIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
        }

        private void Exit_Click(object sender, EventArgs e)
        {
            StopPingTimer();
            authListener?.Stop();
            trayIcon.Visible = false;
            httpClient?.Dispose();
            Application.Exit();
        }

        private string GetSuccessHtml(string username, string shortId)
        {
            return $@"<!DOCTYPE html><html lang='en'><head><meta charset='UTF-8'><meta name='viewport' content='width=device-width, initial-scale=1.0'><title>Authorization Successful</title><style>*{{margin:0;padding:0;box-sizing:border-box}}body{{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;min-height:100vh;display:flex;justify-content:center;align-items:center;background:linear-gradient(to bottom right,#f0f9ff,#ffffff,#e0f2fe);padding:16px}}.container{{background:white;padding:40px;border-radius:16px;text-align:center;box-shadow:0 25px 50px -12px rgba(0,0,0,0.25);max-width:448px;width:100%;animation:scaleIn 0.3s ease-out}}@keyframes scaleIn{{from{{transform:scale(0.95);opacity:0}}to{{transform:scale(1);opacity:1}}}}.success-icon{{width:80px;height:80px;margin:0 auto 24px;background:linear-gradient(135deg,#3b82f6 0%,#2563eb 100%);border-radius:50%;display:flex;align-items:center;justify-content:center;animation:iconPop 0.5s ease-out 0.2s backwards}}@keyframes iconPop{{0%{{transform:scale(0)}}50%{{transform:scale(1.1)}}100%{{transform:scale(1)}}}}.checkmark{{font-size:48px;color:white}}h1{{color:#111827;font-size:28px;font-weight:700;margin-bottom:8px}}.subtitle{{color:#6b7280;font-size:16px;line-height:1.5;margin-bottom:32px}}.info-box{{background:#f9fafb;border:1px solid #e5e7eb;border-radius:12px;padding:20px;margin-bottom:24px}}.info-item{{display:flex;justify-content:space-between;align-items:center;padding:8px 0}}.info-item:not(:last-child){{border-bottom:1px solid #e5e7eb;margin-bottom:8px}}.info-label{{color:#6b7280;font-size:14px;font-weight:500}}.info-value{{color:#111827;font-size:14px;font-weight:600}}.status-badge{{display:inline-flex;align-items:center;gap:6px;background:#dcfce7;color:#166534;padding:4px 12px;border-radius:9999px;font-size:12px;font-weight:600}}.status-dot{{width:6px;height:6px;background:#16a34a;border-radius:50%;animation:pulse 2s infinite}}@keyframes pulse{{0%,100%{{opacity:1}}50%{{opacity:0.5}}}}.closing-message{{color:#6b7280;font-size:14px;margin-top:24px;padding-top:24px;border-top:1px solid #e5e7eb}}.countdown{{display:inline-flex;align-items:center;justify-content:center;width:24px;height:24px;background:#3b82f6;color:white;border-radius:50%;font-weight:600;font-size:12px;margin-left:4px}}</style></head><body><div class='container'><div class='success-icon'><div class='checkmark'>✓</div></div><h1>Authorization Successful!</h1><p class='subtitle'>Your device has been successfully linked to your McWhitelist account.</p><div class='info-box'><div class='info-item'><span class='info-label'>Username</span><span class='info-value'>{username} ({shortId})</span></div><div class='info-item'><span class='info-label'>Device</span><span class='info-value'>Desktop Application</span></div><div class='info-item'><span class='info-label'>Status</span><span class='status-badge'><span class='status-dot'></span>Connected</span></div></div><p class='closing-message'>You can close this window<br><span style='font-size:13px;color:#9ca3af'>Closing automatically in <span class='countdown' id='countdown'>3</span></span></p></div><script>let seconds=3;const countdownEl=document.getElementById('countdown');const timer=setInterval(()=>{{seconds--;countdownEl.textContent=seconds;if(seconds<=0){{clearInterval(timer);window.close()}}}},1000);</script></body></html>";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopPingTimer();
                authListener?.Stop();
                trayIcon?.Dispose();
                httpClient?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}