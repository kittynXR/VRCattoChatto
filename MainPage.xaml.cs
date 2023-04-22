using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using CoreOSC;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Events;

namespace VRCatNet
{
    public class CustomToggleButton : ToggleButton
    {
        public CustomToggleButton()
        {
            DefaultStyleKey = typeof(CustomToggleButton);
            UpdateButtonColor();
            Checked += CustomToggleButton_Checked;
            Unchecked += CustomToggleButton_Unchecked;
        }

        private void CustomToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            UpdateButtonColor();
        }

        private void CustomToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateButtonColor();
        }

        public void UpdateButtonColor()
        {
            if (IsChecked == true)
                Background = new SolidColorBrush(Colors.Blue);
            else
                Background = new SolidColorBrush(Colors.DarkMagenta);
        }

        public void SetTypingColor(bool isTyping)
        {
            if (IsChecked == true)
                Background = isTyping ? new SolidColorBrush(Colors.Green) : new SolidColorBrush(Colors.Blue);
            else
                Background = new SolidColorBrush(Colors.DarkMagenta);
        }
    }

    public sealed partial class MainPage : Page
    {
        public static readonly DependencyProperty MaxCharactersProperty =
            DependencyProperty.Register("MaxCharacters", typeof(int), typeof(MainPage), new PropertyMetadata(500));

        private readonly DispatcherTimer typingTimer;
        private bool audioEnabled;

        private UDPSender oscSender;
        private bool pauseScroll;
        private TwitchClient twitchClient;

        private string storedBroadcasterName;

        private SemaphoreSlim uiSemaphore = new SemaphoreSlim(1, 1);
        private bool messageSentByApp;
        private bool isSendingMessage = false;


        public MainPage()
        {
            InitializeComponent();
            InitializeOsc();
            toggleTyping.UpdateButtonColor();

            typingTimer = new DispatcherTimer();
            typingTimer = new DispatcherTimer();
            typingTimer = new DispatcherTimer();
            typingTimer.Interval =
                TimeSpan.FromSeconds(1); // Set the interval to 1 second, or change it to the desired delay
            typingTimer.Tick += TypingTimer_Tick;

            // Add event handlers for the send button and return key
            sendButton.Click += SendButton_Click;
            textInput.KeyDown += TextInput_KeyUp;
            clearInputButton.Click += ClearInputButton_Click;
            clearOscEndpointButton.Click += ClearOscEndpointButton_Click;

            UpdateCharacterCounter();
        }

        public int MaxCharacters
        {
            get => (int)GetValue(MaxCharactersProperty);
            set => SetValue(MaxCharactersProperty, value);
        }

        private void InitializeOsc()
        {
            var ipAddress = "127.0.0.1";
            var port = 9000;
            // Replace the IP and port with your OSC server's IP and port
            oscSender = new UDPSender(ipAddress, port);
        }

        private async Task InitializeTwitchClient()
        {
            try
            {
                // Retrieve the stored OAuth key and broadcaster name
                var localSettings = ApplicationData.Current.LocalSettings;
                var storedOAuthKey = localSettings.Values["OAuthKey"] as string;
                storedBroadcasterName = localSettings.Values["BroadcasterName"] as string;

                if (!string.IsNullOrEmpty(storedOAuthKey) && !string.IsNullOrEmpty(storedBroadcasterName))
                {
                    // Configure the Twitch client
                    var credentials = new ConnectionCredentials(storedBroadcasterName, storedOAuthKey);
                    twitchClient = new TwitchClient();
                    twitchClient.Initialize(credentials, storedBroadcasterName);

                    // Subscribe to relevant events
                    twitchClient.OnMessageReceived += TwitchClient_OnMessageReceived;
                    twitchClient.OnJoinedChannel += TwitchClient_OnJoinedChannel;
                    twitchClient.OnConnected += TwitchClient_OnConnected;
                    twitchClient.OnDisconnected += TwitchClient_OnDisconnected;

                    // Connect to Twitch
                    if (twitchClient != null) await ConnectTwitchClientAsync(twitchClient);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InitializeTwitchClient exception: {ex.Message}");
            }
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeTwitchClient();
        }

        private async Task ConnectTwitchClientAsync(TwitchClient twitchClient)
        {
            await Task.Run(() => twitchClient.Connect());
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            try
            {
                base.OnNavigatedTo(e);
                //Task.Run(async () => await InitializeTwitchClient());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OnNavigatedTo exception: {ex.Message}");
            }
        }

        private async void initTwitchButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await InitializeTwitchClient();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"initTwitchButton_Click exception: {ex.Message}");
            }
            textInput.Focus(FocusState.Programmatic);
        }

        private async void TwitchClient_OnConnected(object sender, OnConnectedArgs e)
        {
            // Join the channel after connecting to Twitch
            twitchClient.JoinChannel("#" + storedBroadcasterName);

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                () => { textHistory.Text += "Connected to Twitch chat.\n"; });
        }

        private async void TwitchClient_OnJoinedChannel(object sender, OnJoinedChannelArgs e)
        {
            Debug.WriteLine($"Joined channel: {e.Channel}");

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                () => { textHistory.Text += $"Joined channel: {e.Channel}\n"; });
        }

        private void TwitchClient_OnDisconnected(object sender, OnDisconnectedEventArgs e)
        {
            // Handle disconnection, e.g., update UI or attempt to reconnect
        }

        private async void TwitchClient_OnMessageReceived(object sender, OnMessageReceivedArgs e)
        {
            await uiSemaphore.WaitAsync(); // Wait for the semaphore

            if (messageSentByApp &&
                e.ChatMessage.Username.Equals(storedBroadcasterName, StringComparison.OrdinalIgnoreCase))
            {
                messageSentByApp = false;
                return;
            }

            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    // Update the text history with the received chat message
                    //textHistory.Text += $"{e.ChatMessage.Username}: {e.ChatMessage.Message}\n";
                    UpdateTextHistory(e.ChatMessage.Username, e.ChatMessage.Message);
                    ScrollToBottom();
                });
            }
            finally
            {
                uiSemaphore.Release(); // Release the semaphore
            }
        }

        private void UpdateTextHistory(string username, string message)
        {
            // Update the text history with the sent message
            textHistory.Text += $"{username}: {message}\n";
            if(!pauseScroll)
            ScrollToBottom();
        }

        private void TextInput_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter && !isSendingMessage)
            {
                isSendingMessage = true;
                e.Handled = true;
                SendMessage();
                isSendingMessage = false;
            }

            // Send a True signal to the /chatbox/typing OSC endpoint
            if (toggleOsc.IsChecked.Value && toggleTyping.IsChecked.Value)
                oscSender.Send(new OscMessage("/chatbox/typing", true));
        }

        private void TypingTimer_Tick(object sender, object e)
        {
            // Set the /chatbox/typing OSC endpoint to false when the timer ticks
            if (toggleOsc.IsChecked.Value) oscSender.Send(new OscMessage("/chatbox/typing", false));

            //toggleTyping.UpdateButtonColor();
            toggleTyping.SetTypingColor(false);

            typingTimer.Stop(); // Stop the timer
        }

        private void textInput_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // ...

            // Start/reset the timer when a key is pressed
            if (toggleOsc.IsChecked.Value && toggleTyping.IsChecked.Value)
            {
                typingTimer.Stop(); // Stop the timer if it's running
                typingTimer.Start(); // Start/reset the timer
                //toggleTyping.Background = new SolidColorBrush(Windows.UI.Colors.Green);
                toggleTyping.SetTypingColor(true);
            }
            else
            {
                toggleTyping.UpdateButtonColor();
            }
        }

        private void toggleTyping_Checked(object sender, RoutedEventArgs e)
        {
            toggleTyping.Background = new SolidColorBrush(Colors.LightSeaGreen);
        }

        private void toggleTyping_Unchecked(object sender, RoutedEventArgs e)
        {
            toggleTyping.Background = new SolidColorBrush(Colors.LightGray);
            oscSender.Send(new OscMessage("/chatbox/typing", false));
        }

        private void UpdateCharacterCounter()
        {
            var charactersRemaining = MaxCharacters - textInput.Text.Length;
            characterCounter.Text = $"{charactersRemaining}/{MaxCharacters}";

            if (charactersRemaining <= MaxCharacters * 0.15)
                characterCounter.Foreground = new SolidColorBrush(Colors.Red);
            else
                characterCounter.Foreground = new SolidColorBrush(Colors.White);
        }

        private void textInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateCharacterCounter();
        }

        private void ScrollToBottom()
        {
            if (!pauseScroll)
            {
                // Scroll the textHistoryScrollViewer to the bottom
                var verticalOffset = textHistoryScrollViewer.ExtentHeight - textHistoryScrollViewer.ViewportHeight;
                textHistoryScrollViewer.ChangeView(null, verticalOffset, null, true);
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isSendingMessage)
            {
                isSendingMessage = true;
                messageSentByApp = true;
                SendMessage();
                isSendingMessage = false;
            }
        }

        private void ClearInputButton_Click(object sender, RoutedEventArgs e)
        {
            // Clear the text input
            textInput.Text = "";
        }

        private void ClearOscEndpointButton_Click(object sender, RoutedEventArgs e)
        {
            // Send an empty string to the /chatbox/input OSC endpoint
            oscSender.Send(new OscMessage("/chatbox/input", ""));
        }

        private void SendMessage()
        {

            if (textInput.Text == "") return;
            // Send message to Twitch chat if the toggle is on
            if (toggleTwitch.IsChecked.Value) twitchClient.SendMessage("#" + storedBroadcasterName, textInput.Text);

            // Send message as an OSC endpoint if the toggle is on
            if (toggleOsc.IsChecked.Value)
                try
                {
                    var txt = textInput.Text;

                    object[] args = { textInput.Text, true, audioEnabled };
                    var message = new OscMessage("/chatbox/input", args);
                    oscSender.Send(message);
                }
                catch (Exception ex)
                {
                    // Log the error or display a message to the user
                    Debug.WriteLine($"Error sending OSC message: {ex.Message}");
                }

            // Update the text history with the sent message
            UpdateTextHistory(storedBroadcasterName, textInput.Text);
            ScrollToBottom();

            // Clear the text input
            textInput.Text = "";

            textInput.Focus(FocusState.Programmatic);
        }

        private void toggleAudio_Checked(object sender, RoutedEventArgs e)
        {
            audioEnabled = true;
        }

        private void toggleAudio_Unchecked(object sender, RoutedEventArgs e)
        {
            audioEnabled = false;
        }

        private void togglePauseScroll_Checked(object sender, RoutedEventArgs e)
        {
            pauseScroll = true;
        }

        private void togglePauseScroll_Unchecked(object sender, RoutedEventArgs e)
        {
            pauseScroll = false;
            ScrollToBottom(); // Scroll to the bottom when the pause is released
        }

        private async void oauthButton_Click(object sender, RoutedEventArgs e)
        {
            // Retrieve the stored broadcaster name
            var localSettings = ApplicationData.Current.LocalSettings;
            var storedBroadcasterName = localSettings.Values["BroadcasterName"] as string;

            // Create input fields for entering the broadcaster OAuth key and name
            var oauthInput = new PasswordBox { PlaceholderText = "OAuth key" };
            var broadcasterNameInput = new TextBox
                { PlaceholderText = "Broadcaster name", Text = storedBroadcasterName ?? "" };
            var showOauthButton = new Button { Content = "Show OAuth key" };

            showOauthButton.Click += (s, args) =>
            {
                if (oauthInput.PasswordRevealMode == PasswordRevealMode.Hidden)
                {
                    oauthInput.PasswordRevealMode = PasswordRevealMode.Visible;
                    showOauthButton.Content = "Hide OAuth key";
                }
                else
                {
                    oauthInput.PasswordRevealMode = PasswordRevealMode.Hidden;
                    showOauthButton.Content = "Show OAuth key";
                }
            };

            // Create a HyperlinkButton for the OAuth token generator
            var oauthTokenGeneratorLink = new HyperlinkButton
            {
                Content = "Generate OAuth token",
                NavigateUri = new Uri("https://twitchapps.com/tmi/"),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            // Create a new input dialog for entering the broadcaster OAuth key and name
            var oauthDialog = new ContentDialog
            {
                Title = "Enter OAuth key and Broadcaster name",
                Content = new StackPanel
                {
                    Children = { oauthInput, broadcasterNameInput, showOauthButton, oauthTokenGeneratorLink }
                },
                PrimaryButtonText = "OK",
                SecondaryButtonText = "Cancel"
            };

            // Show the dialog and update the Twitch client's OAuth key and broadcaster name if provided
            var result = await oauthDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                if (!string.IsNullOrWhiteSpace(oauthInput.Password) &&
                    !string.IsNullOrWhiteSpace(broadcasterNameInput.Text))
                {
                    // Update the Twitch client's OAuth key and broadcaster name, then reconnect
                    //twitchClient.Disconnect();
                    //  twitchClient.Initialize(new ConnectionCredentials(broadcasterNameInput.Text, oauthInput.Password));//, broadcasterNameInput.Text);
                    // Connect to Twitch asynchronously
                    //   await ConnectTwitchClientAsync(twitchClient);

                    localSettings.Values["OAuthKey"] = oauthInput.Password;
                    localSettings.Values["BroadcasterName"] = broadcasterNameInput.Text;
                    textInput.Focus(FocusState.Programmatic);
                }
            // Store the updated OAuth key and broadcaster name
        }
    }
}