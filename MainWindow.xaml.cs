using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Finder
{
    /// <summary>
    /// Модель данных для отображения в ListView.
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
        // ---------------- Параметры протокола ----------------
        private const int DiscoveryPort = 23;
        private const byte TagCmd = 0xFF;           // маркер запроса
        private const byte TagStatus = 0xFE;        // маркер ответа
        private const byte CmdDiscoverTarget = 0x02;

        // ---------------- Параметры сканирования ----------------
        private const int MaxBoards = 256;
        private const int FirstReplyTimeoutMs = 5000;   // ожидание первого ответа
        private const int SubsequentTimeoutMs = 1000;   // ожидание следующих ответов
        private const int OverallTimeoutMs = 15000;     // жёсткий предел всего сканирования

        /// <summary>Коллекция для привязки данных.</summary>
        public ObservableCollection<BoardInfo> Boards { get; } = new ObservableCollection<BoardInfo>();

        /// <summary>Защита от повторного запуска сканирования (порт 23 занят первым проходом).</summary>
        private bool _isScanning;

        // Импорт функции SendARP из iphlpapi.dll (только Windows)
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddress, ref uint macAddressLength);

        public MainWindow()
        {
            InitializeComponent();
            BoardListView.ItemsSource = Boards;
        }

        // ------------------------------------------------------------------
        //  Обработчики UI
        // ------------------------------------------------------------------

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // На старте не показываем модальное окно "плат не найдено" — только строку статуса.
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
        /// Двойной клик по строке списка — открыть веб-интерфейс платы.
        /// Берём плату из строки, по которой кликнули, а не из SelectedItem.
        /// </summary>
        private void ListViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListViewItem { DataContext: BoardInfo board })
                OpenBoardInBrowser(board);
        }

        /// <summary>
        /// Открывает веб-интерфейс платы в браузере по умолчанию.
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
                    UseShellExecute = true   // необходимо для открытия URL в браузере
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
        //  Сканирование
        // ------------------------------------------------------------------

        /// <summary>
        /// Результат фонового сканирования. Сообщения об ошибках возвращаются сюда,
        /// а показываются уже в UI-потоке.
        /// </summary>
        private sealed class ScanResult
        {
            public List<BoardInfo> Boards { get; } = new List<BoardInfo>();
            public string? Error { get; set; }
        }

        /// <summary>Локальный сокет вместе с адресом интерфейса и его широковещательным адресом.</summary>
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
        /// Асинхронно обновляет список плат, запуская сканирование в фоновом потоке.
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

                // Старый список остаётся видимым во время сканирования и заменяется только здесь.
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
        /// Основной метод сканирования сети. Выполняется в фоновом потоке
        /// и не обращается к UI напрямую.
        /// </summary>
        private ScanResult DiscoverBoards()
        {
            var result = new ScanResult();
            var scanSockets = new List<ScanSocket>();

            try
            {
                // 1. Активные IPv4-адреса (кроме loopback и туннелей)
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

                        interfaces.Add((ua.Address, GetBroadcastAddress(ua)));
                    }
                }

                if (interfaces.Count == 0)
                {
                    result.Error = "No active IPv4 network interfaces found.";
                    return result;
                }

                // 2. Сокет на каждый интерфейс. Порт 23 может быть занят —
                //    в этом случае пробуем эфемерный порт (демон отвечает на адрес отправителя).
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

                // 3. Запрос: TAG_CMD, длина, команда, контрольная сумма.
                //    Длина включает байт контрольной суммы; сумма всех байт пакета = 0 (mod 256).
                byte[] request = new byte[4];
                request[0] = TagCmd;
                request[1] = 4;
                request[2] = CmdDiscoverTarget;
                unchecked
                {
                    request[3] = (byte)(-(request[0] + request[1] + request[2]));
                }

                // 4. Рассылаем через каждый сокет: и на 255.255.255.255,
                //    и на широковещательный адрес подсети (некоторые адаптеры
                //    отбрасывают limited broadcast).
                foreach (var entry in scanSockets)
                {
                    foreach (var target in GetBroadcastTargets(entry))
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

                // 5. Ждём ответы. Помимо таймаута простоя действует жёсткий общий предел,
                //    иначе посторонний трафик на порту 23 бесконечно продлевает цикл.
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
                        Socket.Select(checkRead, null, null, waitMs * 1000);
                    }
                    catch (SocketException ex)
                    {
                        Debug.WriteLine($"Select failed: {ex.Message}");
                        break;
                    }

                    if (checkRead.Count == 0)
                        break;   // таймаут простоя

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

                    // После первого найденного устройства ждём остальные меньше.
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
        /// Создаёт UDP-сокет на заданном адресе и порту. Возвращает null, если привязка не удалась.
        /// </summary>
        private static Socket? TryBind(IPAddress local, int port)
        {
            Socket? sock = null;
            try
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                // Разрешаем повторную привязку: иначе второй запуск сканирования
                // падает с "адрес уже используется".
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

        /// <summary>Адреса, по которым рассылается запрос обнаружения.</summary>
        private static List<IPEndPoint> GetBroadcastTargets(ScanSocket entry)
        {
            var targets = new List<IPEndPoint>
            {
                new IPEndPoint(IPAddress.Broadcast, DiscoveryPort)
            };

            if (entry.Broadcast != null &&
                !entry.Broadcast.Equals(IPAddress.Broadcast) &&
                !entry.Broadcast.Equals(IPAddress.Any))
            {
                targets.Add(new IPEndPoint(entry.Broadcast, DiscoveryPort));
            }

            return targets;
        }

        /// <summary>Вычисляет широковещательный адрес подсети по адресу и маске.</summary>
        private static IPAddress? GetBroadcastAddress(UnicastIPAddressInformation ua)
        {
            try
            {
                byte[] addr = ua.Address.GetAddressBytes();
                byte[]? mask = ua.IPv4Mask?.GetAddressBytes();

                if (mask == null || mask.Length != 4 || addr.Length != 4)
                    return null;

                byte[] broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcast[i] = (byte)((addr[i] | ~mask[i]) & 0xFF);

                return new IPAddress(broadcast);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetBroadcastAddress failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Разбирает ответ платы.
        /// Формат: 0 TAG_STATUS, 1 length, 2 CMD, 3 board type, 4 board ID,
        /// 5-8 client IP, 9-14 MAC, 15-18 version, 19-82 app title, далее контрольная сумма.
        /// Поле length включает байт контрольной суммы.
        /// </summary>
        private void ProcessResponse(byte[] buffer, int received, IPAddress remoteAddress, List<BoardInfo> boards)
        {
            if (received < 4)
                return;

            if (buffer[0] != TagStatus || buffer[2] != CmdDiscoverTarget)
                return;

            int length = buffer[1];
            if (length < 4 || length > received)
                return;

            // Сумма всех байт пакета должна быть 0 (mod 256)
            int sum = 0;
            for (int i = 0; i < length; i++)
                sum += buffer[i];
            if ((sum & 0xFF) != 0)
                return;

            string ip = remoteAddress.ToString();
            if (boards.Any(b => b.Ip == ip))
                return;   // плата ответила на несколько наших сокетов

            var board = new BoardInfo { Ip = ip };

            if (length > 9)
            {
                byte[] clientIpBytes = new byte[4];
                Array.Copy(buffer, 5, clientIpBytes, 0, 4);
                board.ClientIp = new IPAddress(clientIpBytes).ToString();
            }

            if (length > 15)
            {
                byte[] macBytes = new byte[6];
                Array.Copy(buffer, 9, macBytes, 0, 6);
                board.Mac = BitConverter.ToString(macBytes).Replace('-', ':');
            }
            else
            {
                board.Mac = GetMacByArp(remoteAddress);
            }

            if (length > 19)
                board.Version = $"{buffer[15]}.{buffer[16]}.{buffer[17]}.{buffer[18]}";

            if (length > 83)
            {
                // Обрезаем по ПЕРВОМУ нулевому байту: за терминатором в буфере
                // демона может остаться мусор. Декодируем UTF-8, а не ASCII.
                const int titleOffset = 19;
                const int titleSize = 64;

                int end = titleOffset;
                while (end < titleOffset + titleSize && buffer[end] != 0)
                    end++;

                board.App = Encoding.UTF8.GetString(buffer, titleOffset, end - titleOffset).Trim();
            }

            boards.Add(board);
        }

        /// <summary>
        /// Получает MAC-адрес по IP через ARP (только Windows, только IPv4).
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
