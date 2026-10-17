using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Finder
{
    /// <summary>
    /// Data model for display in the ListView.
    /// </summary>
    public class BoardInfo : INotifyPropertyChanged
    {
        private string _ip = string.Empty;
        private string _mac = string.Empty;
        private string _clientIp = string.Empty;
        private string _version = string.Empty;
        private string _app = string.Empty;

        public string Ip
        {
            get => _ip;
            set { _ip = value ?? string.Empty; OnPropertyChanged(nameof(Ip)); }
        }

        public string Mac
        {
            get => _mac;
            set { _mac = value ?? string.Empty; OnPropertyChanged(nameof(Mac)); }
        }

        public string ClientIp
        {
            get => _clientIp;
            set { _clientIp = value ?? string.Empty; OnPropertyChanged(nameof(ClientIp)); }
        }

        public string Version
        {
            get => _version;
            set { _version = value ?? string.Empty; OnPropertyChanged(nameof(Version)); }
        }

        public string App
        {
            get => _app;
            set { _app = value ?? string.Empty; OnPropertyChanged(nameof(App)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }

    public partial class MainWindow : Window
    {
        // ---------------- Protocol parameters ----------------
        private const int DiscoveryPort = DiscoveryProtocol.DiscoveryPort;

        // ---------------- Scan parameters ----------------
        private const int MaxBoards = 256;
        private const int FirstReplyTimeoutMs = 5000;   // wait for the first reply
        private const int SubsequentTimeoutMs = 1000;   // wait for subsequent replies
        private const int OverallTimeoutMs = 15000;     // hard limit for the whole scan

        /// <summary>Collection for data binding.</summary>
        public ObservableCollection<BoardInfo> Boards { get; } = new ObservableCollection<BoardInfo>();

        /// <summary>Guards against re-entrant scanning (port 23 is held by the first pass).</summary>
        private bool _isScanning;

        // Import of the SendARP function from iphlpapi.dll (Windows only)
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddress, ref uint macAddressLength);

        public MainWindow()
        {
            InitializeComponent();
            BoardListView.ItemsSource = Boards;
        }

        // ------------------------------------------------------------------
        //  UI handlers
        // ------------------------------------------------------------------

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Don't show the "no boards found" dialog on startup — status line only.
            await RefreshBoardsAsync(showEmptyDialog: false);
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshBoardsAsync(showEmptyDialog: true);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        /// <summary>
        /// Double-click on a list row — open the board's web interface.
        /// The board is taken from the clicked row, not from SelectedItem.
        /// </summary>
        private void ListViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListViewItem { DataContext: BoardInfo board })
                OpenBoardInBrowser(board);
        }

        /// <summary>
        /// Opens the board's web interface in the default browser.
        /// </summary>
        private void OpenBoardInBrowser(BoardInfo board)
        {
            if (board == null)
                return;

            string ip = board.Ip.Trim();

            if (string.IsNullOrEmpty(ip) || ip == "0.0.0.0" || !IPAddress.TryParse(ip, out _))
            {
                MessageBox.Show(this, $"Invalid IP address: '{ip}'", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string url = $"http://{ip}";

                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true   // required to open the URL in a browser
                });

                Debug.WriteLine($"Opening browser for: {url}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to open browser: {ex.Message}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ------------------------------------------------------------------
        //  Scanning
        // ------------------------------------------------------------------

        /// <summary>
        /// Result of the background scan. Error messages are returned here
        /// and displayed once back on the UI thread.
        /// </summary>
        private sealed class ScanResult
        {
            public List<BoardInfo> Boards { get; } = new List<BoardInfo>();
            public string? Error { get; set; }
        }

        /// <summary>A local socket paired with its interface address and that interface's broadcast address.</summary>
        private sealed class ScanSocket
        {
            public ScanSocket(Socket socket, IPAddress local, IPAddress? broadcast)
            {
                Socket = socket;
                Local = local;
                Broadcast = broadcast;
            }

            public Socket Socket { get; }
            public IPAddress Local { get; }
            public IPAddress? Broadcast { get; }
        }

        /// <summary>
        /// Asynchronously refreshes the board list by running the scan on a background thread.
        /// </summary>
        private async Task RefreshBoardsAsync(bool showEmptyDialog)
        {
            if (_isScanning)
                return;

            _isScanning = true;
            RefreshButton.IsEnabled = false;
            StatusText.Text = "Scanning the network...";
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                ScanResult result = await Task.Run(DiscoverBoards);

                // The old list stays visible during the scan and is replaced only here.
                Boards.Clear();
                foreach (var board in result.Boards)
                    Boards.Add(board);

                if (!string.IsNullOrEmpty(result.Error))
                {
                    StatusText.Text = result.Error;
                    MessageBox.Show(this, result.Error, "Error",
                                    MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else if (result.Boards.Count == 0)
                {
                    StatusText.Text = "No boards found.";
                    if (showEmptyDialog)
                        MessageBox.Show(this, "No boards found.", "Information",
                                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    StatusText.Text = $"Boards found: {result.Boards.Count}";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Scan failed.";
                MessageBox.Show(this, $"Scan failed: {ex.Message}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                RefreshButton.IsEnabled = true;
                _isScanning = false;
            }
        }

        /// <summary>
        /// Main network scan method. Runs on a background thread
        /// and never touches the UI directly.
        /// </summary>
        private ScanResult DiscoverBoards()
        {
            var result = new ScanResult();
            var scanSockets = new List<ScanSocket>();

            try
            {
                // 1. Active IPv4 addresses (excluding loopback and tunnels)
                var interfaces = new List<(IPAddress Local, IPAddress? Broadcast)>();

                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    IPInterfaceProperties props;
                    try { props = ni.GetIPProperties(); }
                    catch (Exception ex) { Debug.WriteLine($"GetIPProperties failed: {ex.Message}"); continue; }

                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;
                        if (interfaces.Any(i => i.Local.Equals(ua.Address)))
                            continue;

                        interfaces.Add((ua.Address, DiscoveryProtocol.GetBroadcastAddress(ua.Address, ua.IPv4Mask)));
                    }
                }

                if (interfaces.Count == 0)
                {
                    result.Error = "No active IPv4 network interfaces found.";
                    return result;
                }

                // 2. A socket per interface. Port 23 may already be in use —
                //    in that case fall back to an ephemeral port (the daemon replies to the sender's address).
                foreach (var iface in interfaces)
                {
                    Socket? sock = TryBind(iface.Local, DiscoveryPort) ?? TryBind(iface.Local, 0);
                    if (sock == null)
                    {
                        Debug.WriteLine($"Could not bind any port on {iface.Local}");
                        continue;
                    }
                    scanSockets.Add(new ScanSocket(sock, iface.Local, iface.Broadcast));
                }

                if (scanSockets.Count == 0)
                {
                    result.Error = "Could not open any UDP socket (port 23 may be in use).";
                    return result;
                }

                // 3. Request: TAG_CMD, length, command, checksum.
                byte[] request = DiscoveryProtocol.BuildRequest();

                // 4. Broadcast through every socket: to 255.255.255.255
                //    and to the subnet's broadcast address (some adapters
                //    drop the limited broadcast).
                foreach (var entry in scanSockets)
                {
                    foreach (var target in DiscoveryProtocol.GetBroadcastTargets(entry.Broadcast))
                    {
                        try
                        {
                            entry.Socket.SendTo(request, target);
                            Debug.WriteLine($"Discovery request {entry.Local} -> {target}");
                        }
                        catch (SocketException ex)
                        {
                            Debug.WriteLine($"Send to {target} failed: {ex.Message}");
                        }
                    }
                }

                // 5. Wait for replies. Besides the idle timeout, a hard overall deadline applies,
                //    otherwise unrelated traffic on port 23 could extend the loop indefinitely.
                var sockets = scanSockets.Select(s => s.Socket).ToList();
                var stopwatch = Stopwatch.StartNew();
                int idleTimeoutMs = FirstReplyTimeoutMs;
                byte[] buffer = new byte[2048];

                while (result.Boards.Count < MaxBoards)
                {
                    long remainingMs = OverallTimeoutMs - stopwatch.ElapsedMilliseconds;
                    if (remainingMs <= 0)
                    {
                        Debug.WriteLine("Overall scan deadline reached");
                        break;
                    }

                    int waitMs = (int)Math.Min(idleTimeoutMs, remainingMs);
                    var checkRead = new List<Socket>(sockets);

                    try
                    {
                        Socket.Select(checkRead, null, null, waitMs);
                    }
                    catch (SocketException ex)
                    {
                        Debug.WriteLine($"Select failed: {ex.Message}");
                        break;
                    }

                    if (checkRead.Count == 0)
                        break;   // idle timeout

                    foreach (Socket sock in checkRead)
                    {
                        try
                        {
                            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                            int received = sock.ReceiveFrom(buffer, ref remote);
                            if (remote is IPEndPoint remoteEp)
                                ProcessResponse(buffer, received, remoteEp.Address, result.Boards);
                        }
                        catch (SocketException ex)
                        {
                            Debug.WriteLine($"Receive failed: {ex.Message}");
                        }
                    }

                    // Wait less for the remaining devices once the first one is found.
                    if (idleTimeoutMs != SubsequentTimeoutMs && result.Boards.Count > 0)
                        idleTimeoutMs = SubsequentTimeoutMs;
                }
            }
            catch (Exception ex)
            {
                result.Error = $"Network scan error: {ex.Message}";
            }
            finally
            {
                foreach (var entry in scanSockets)
                {
                    try { entry.Socket.Dispose(); }
                    catch (Exception ex) { Debug.WriteLine($"Socket dispose failed: {ex.Message}"); }
                }
            }

            return result;
        }

        /// <summary>
        /// Creates a UDP socket on the given address and port. Returns null if the bind failed.
        /// </summary>
        private static Socket? TryBind(IPAddress local, int port)
        {
            Socket? sock = null;
            try
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                // Allow re-binding: otherwise a second scan run
                // fails with "address already in use".
                try { sock.ExclusiveAddressUse = false; }
                catch (Exception ex) { Debug.WriteLine($"ExclusiveAddressUse: {ex.Message}"); }
                try { sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); }
                catch (Exception ex) { Debug.WriteLine($"ReuseAddress: {ex.Message}"); }

                sock.EnableBroadcast = true;
                sock.ReceiveTimeout = 1000;
                sock.Bind(new IPEndPoint(local, port));
                return sock;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Bind {local}:{port} failed - {ex.Message}");
                sock?.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Parses a board's response and appends it to the list (deduplicating by IP).
        /// The wire format itself is handled by <see cref="DiscoveryProtocol.TryParseResponse"/>.
        /// </summary>
        private void ProcessResponse(byte[] buffer, int received, IPAddress remoteAddress, List<BoardInfo> boards)
        {
            if (!DiscoveryProtocol.TryParseResponse(buffer, received, remoteAddress, GetMacByArp, out var board))
                return;

            string ip = remoteAddress.ToString();
            if (boards.Any(b => b.Ip == ip))
                return;   // the board replied to more than one of our sockets

            boards.Add(board!);
        }

        /// <summary>
        /// Resolves a MAC address from an IP via ARP (Windows only, IPv4 only).
        /// </summary>
        private string GetMacByArp(IPAddress ip)
        {
            try
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork)
                    return "N/A";

                byte[] addressBytes = ip.GetAddressBytes();
                if (addressBytes.Length != 4)
                    return "N/A";

                uint destIp = BitConverter.ToUInt32(addressBytes, 0);
                byte[] mac = new byte[6];
                uint len = (uint)mac.Length;

                if (SendARP(destIp, 0, mac, ref len) == 0 && len == 6)
                    return BitConverter.ToString(mac, 0, 6).Replace('-', ':');
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SendARP failed: {ex.Message}");
            }

            return "N/A";
        }
    }
}
