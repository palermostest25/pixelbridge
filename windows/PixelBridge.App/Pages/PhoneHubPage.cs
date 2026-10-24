using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PixelBridge.Core;

namespace PixelBridge.App.Pages;

/// <summary>Native communications and utility surfaces. No phone data is polled while hidden.</summary>
public sealed class PhoneHubPage : UserControl, IPage
{
    private readonly AppController _c;
    private readonly string _mode;
    private readonly StackPanel _root = new();
    private readonly TextBlock _notice = new(), _receipt = new(), _detail = new();
    private readonly ListBox _list = new(), _messages = new(), _apps = new(), _files = new();
    private readonly TextBox _number = new(), _draft = new(), _search = new(), _tone = new();
    private readonly Button _send = new(), _answer = new(), _end = new(), _hold = new(), _mute = new();
    private readonly ComboBox _route = new();
    private readonly Slider _volume = new();
    private readonly CheckBox _flash = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _volumeTimer;
    private bool _volumePending;
    private bool _active, _needsLoad = true, _updating, _working;
    private object? _rows, _messageRows, _appRows, _notificationRows;
    private sealed record Row(string Key, string Title, string Detail, object Value);
    private PhoneWorkspace Phone => _c.Phone;

    public PhoneHubPage(AppController controller, string mode)
    {
        _c=controller;_mode=mode;
        _searchTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(280) };
        _volumeTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(220)};
        _volumeTimer.Tick+=async(_,_)=>{_volumeTimer.Stop();await Phone.ToolAsync("tools.volume",new{value=(int)_volume.Value});_volumePending=false;};
        _volume.ValueChanged+=(_,_)=>{if(!_updating&&_active&&_c.Session.IsConnected){_volumePending=true;_volumeTimer.Stop();_volumeTimer.Start();}};
        _searchTimer.Tick+=async(_,_)=>{ _searchTimer.Stop();await Phone.SearchContactsAsync(_search.Text);Tick(); };
        var title=new TextBlock { Text=mode switch {"messages"=>"Messages","calls"=>"Calls","contacts"=>"Contacts","notifications"=>"Notifications",_=>"Phone tools"},FontSize=28,FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,18) };
        _root.Children.Add(title);
        _notice.TextWrapping=TextWrapping.Wrap;_notice.Margin=new(0,0,0,12);_root.Children.Add(_notice);
        var setup=new StackPanel {Orientation=Orientation.Horizontal,Margin=new(0,0,0,16)};
        setup.Children.Add(Button("Set up on phone…",async()=>{ await _c.Commands.OpenSetupAsync(_mode=="notifications"?"notifications":"communications"); _c.LaunchScrcpy(false); }));
        setup.Children.Add(Button("Show phone screen",()=>{_c.LaunchScrcpy(false);return Task.CompletedTask;}));
        setup.Children.Add(Button("Refresh",LoadAsync));_root.Children.Add(setup);
        if(mode=="messages") BuildMessages();else if(mode=="calls")BuildCalls();else if(mode=="contacts")BuildContacts();else if(mode=="notifications")BuildNotifications();else BuildTools();
        Content=_root;
        Loaded+=(_,_)=>SetActive(true);Unloaded+=(_,_)=>SetActive(false);
        Phone.PhoneDataChanged+=()=>Dispatcher.BeginInvoke(()=>{if(_active)_=LoadAsync();});
        _c.Session.StateChanged+=(state,detail)=>{if(state==LinkState.Connected)_needsLoad=true;};
    }
    private Button Button(string label,Func<Task> action)
    {
        var button=new Button {Content=label,Padding=new(12,6,12,6),Margin=new(0,0,8,0),Tag="phoneAction"};
        button.Click+=async(_,_)=>await Run(action);return button;
    }
    private async Task Run(Func<Task> action)
    {
        if(_working)return;
        if(!_c.Session.IsConnected){_detail.Text="Connect the phone first.";return;}
        _working=true;
        try{await action();}catch(Exception e){_detail.Text=e.Message;}finally{_working=false;Tick();}
    }
    private static TextBlock Hint(string text)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,Foreground=SystemColors.GrayTextBrush,Margin=new(0,8,0,12)};
    private static ListBox Prepare(ListBox list,double height)
    {
        list.Height=height;list.BorderThickness=new(0);list.Margin=new(0,0,0,12);
        var stack=new FrameworkElementFactory(typeof(StackPanel));stack.SetValue(FrameworkElement.MarginProperty,new Thickness(8,7,8,7));
        var title=new FrameworkElementFactory(typeof(TextBlock));title.SetBinding(TextBlock.TextProperty,new Binding("Title"));title.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);title.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);stack.AppendChild(title);
        var detail=new FrameworkElementFactory(typeof(TextBlock));detail.SetBinding(TextBlock.TextProperty,new Binding("Detail"));detail.SetValue(TextBlock.FontSizeProperty,12.0);detail.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);detail.SetValue(TextBlock.MarginProperty,new Thickness(0,5,0,0));detail.SetResourceReference(TextBlock.ForegroundProperty,"TextFillColorSecondaryBrush");stack.AppendChild(detail);
        list.ItemTemplate=new DataTemplate{VisualTree=stack};return list;
    }
    private void BuildMessages()
    {
        var top=new StackPanel {Orientation=Orientation.Horizontal};top.Children.Add(Button("New message",()=>{Phone.BeginNewMessage();_number.Text="";_draft.Text="";_list.SelectedIndex=-1;return Task.CompletedTask;}));_root.Children.Add(top);
        var grid=new Grid{Margin=new(0,12,0,0)};grid.ColumnDefinitions.Add(new(){Width=new GridLength(230)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(18)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        grid.Children.Add(Prepare(_list,470));
        _list.SelectionChanged+=async(_,_)=>{if(_updating||_list.SelectedItem is not Row{Value:TextThread thread})return;_number.Text=thread.Address;await Phone.LoadMessagesAsync(thread.Id);Tick();};
        var conversation=new StackPanel();Grid.SetColumn(conversation,2);grid.Children.Add(conversation);
        conversation.Children.Add(Hint("To"));_number.Margin=new(0,0,0,10);conversation.Children.Add(_number);
        conversation.Children.Add(Prepare(_messages,290));_draft.AcceptsReturn=true;_draft.TextWrapping=TextWrapping.Wrap;_draft.Height=85;_draft.MaxLength=12000;_draft.Margin=new(0,0,0,10);conversation.Children.Add(_draft);
        _send.Content="Send SMS";_send.Padding=new(14,7,14,7);_send.HorizontalAlignment=HorizontalAlignment.Right;
        _send.Click+=async(_,_)=>{if(await Phone.SendTextAsync(_number.Text,_draft.Text)){_draft.Clear();await Phone.RefreshThreadsAsync();if(Phone.SelectedThread is long id)await Phone.LoadMessagesAsync(id);}Tick();};conversation.Children.Add(_send);
        _receipt.TextWrapping=TextWrapping.Wrap;_receipt.Margin=new(0,10,0,0);conversation.Children.Add(_receipt);_root.Children.Add(grid);
        _root.Children.Add(Hint("SMS uses your phone’s carrier plan. RCS and other messaging apps can be replied to from Notifications when Android offers a reply action."));
    }
    private void BuildCalls()
    {
        _root.Children.Add(Button("Enable incoming-call controls…", async()=>{await _c.Commands.OpenSetupAsync("calling");_c.LaunchScrcpy(false);}));
        var dial=new StackPanel {Orientation=Orientation.Horizontal};_number.Width=300;_number.FontSize=18;_number.Margin=new(0,0,12,0);dial.Children.Add(_number);dial.Children.Add(Button("Call",async()=>await Phone.CallAsync(_number.Text)));_root.Children.Add(dial);
        _root.Children.Add(Hint("Call audio stays on the phone, speaker or Bluetooth headset. To answer and manage calls here, choose PixelBridge as the default Phone app in phone setup."));
        _root.Children.Add(Prepare(_list,140));
        var actions=new WrapPanel();
        foreach(var b in new[]{_answer,_end,_hold,_mute}){b.Padding=new(12,6,12,6);b.Margin=new(0,0,8,8);actions.Children.Add(b);}
        _answer.Content="Answer";_end.Content="End call";_hold.Content="Hold";_mute.Content="Mute";
        _answer.Click+=async(_,_)=>await CallAction("phone.answer");_end.Click+=async(_,_)=>await CallAction("phone.end");
        _hold.Click+=async(_,_)=>await CallAction(SelectedCall()?.State=="held"?"phone.unhold":"phone.hold");
        _mute.Click+=async(_,_)=>await Phone.CallActionAsync("phone.mute",args:new{enabled=!Phone.Muted});
        _route.ItemsSource=new[]{"phone","speaker","bluetooth"};_route.SelectedIndex=0;_route.Margin=new(0,0,8,8);actions.Children.Add(_route);
        _route.SelectionChanged+=async(_,_)=>{if(!_updating&&Phone.Calls.Count>0)await Phone.CallActionAsync("phone.route",args:new{route=(string)_route.SelectedItem});};
        _tone.Width=55;_tone.MaxLength=1;_tone.Margin=new(0,0,8,8);actions.Children.Add(_tone);
        actions.Children.Add(Button("Send keypad tone",async()=>{if(_tone.Text.Length==1&&"0123456789*#".Contains(_tone.Text))await Phone.CallActionAsync("phone.dtmf",SelectedCall()?.Id,new{digit=_tone.Text});}));_root.Children.Add(actions);
        _root.Children.Add(new TextBlock{Text="Recent calls",FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new(0,20,0,12)});
        _root.Children.Add(Prepare(_messages,300));
        _messages.SelectionChanged+=(_,_)=>{if(_messages.SelectedItem is Row{Value:RecentCall row})_number.Text=row.Number;};
    }
    private PhoneCall? SelectedCall()=>(_list.SelectedItem as Row)?.Value as PhoneCall??Phone.Calls.FirstOrDefault();
    private async Task CallAction(string op){await Phone.CallActionAsync(op,SelectedCall()?.Id);Tick();}
    private void BuildContacts()
    {
        _search.Margin=new(0,0,0,12);_search.TextChanged+=(_,_)=>{if(_active){_searchTimer.Stop();_searchTimer.Start();}};_root.Children.Add(_search);
        _root.Children.Add(Prepare(_list,440));var actions=new StackPanel{Orientation=Orientation.Horizontal};
        actions.Children.Add(Button("Call",async()=>{if(_list.SelectedItem is Row{Value:PhoneContact c})await Phone.CallAsync(c.Number);}));
        actions.Children.Add(Button("Message…",()=>{if(_list.SelectedItem is Row{Value:PhoneContact c})Compose(c.Number,c.Name,null);return Task.CompletedTask;}));_root.Children.Add(actions);
    }
    private void BuildNotifications()
    {
        var settings = new StackPanel { Orientation=Orientation.Horizontal, Margin=new(0,0,0,12) };
        var enabled = new CheckBox { Content="Enable notifications", IsChecked=_c.Settings.NotificationsEnabled, Margin=new(0,0,24,0) };
        var previews = new CheckBox { Content="Show message previews and replies", IsChecked=_c.Settings.NotificationShowContent };
        async void Change(object sender,RoutedEventArgs e) { _c.Settings.NotificationsEnabled=enabled.IsChecked==true; _c.Settings.NotificationShowContent=previews.IsChecked==true; _c.Settings.Save(); Phone.ConfigureNotifications(_c.Settings.NotificationsEnabled,_c.Settings.NotificationShowContent); if (_c.Settings.NotificationsEnabled) await Phone.RefreshNotificationsAsync(); Tick(); }
        enabled.Click+=Change;previews.Click+=Change;settings.Children.Add(enabled);settings.Children.Add(previews);_root.Children.Add(settings);
        _root.Children.Add(Hint("Enable notification access and message previews on the phone to read and reply. Replies are available only when the source app provides Android's text-reply action."));
        _root.Children.Add(Prepare(_list,430));var actions=new StackPanel{Orientation=Orientation.Horizontal};
        actions.Children.Add(Button("Reply…",()=>{if(_list.SelectedItem is Row{Value:PhoneNotification n}&&n.CanReply)Compose("",n.App,n.Key);return Task.CompletedTask;}));
        actions.Children.Add(Button("Dismiss",async()=>{if(_list.SelectedItem is Row{Value:PhoneNotification n})await Phone.DismissAsync(n.Key);}));_root.Children.Add(actions);
    }
    private void Compose(string number,string title,string? replyKey)
    {
        Phone.BeginNewMessage();var window=new Window{Title=replyKey==null?"Message "+title:"Reply in "+title,Width=480,Height=340,Owner=Window.GetWindow(this),WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize};
        var panel=new StackPanel{Margin=new(24)};panel.Children.Add(new TextBlock{Text=title,FontSize=20,FontWeight=FontWeights.SemiBold});panel.Children.Add(Hint(number));
        var body=new TextBox{AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=130,MaxLength=replyKey==null?12000:4000,Margin=new(0,0,0,16)};panel.Children.Add(body);
        var error=new TextBlock{TextWrapping=TextWrapping.Wrap};panel.Children.Add(error);
        var send=new Button{Content=replyKey==null?"Send SMS":"Send reply",HorizontalAlignment=HorizontalAlignment.Right,Padding=new(14,7,14,7)};panel.Children.Add(send);
        send.Click+=async(_,_)=>{send.IsEnabled=false;var ok=replyKey==null?await Phone.SendTextAsync(number,body.Text):await Phone.ReplyAsync(replyKey,body.Text);if(ok)window.Close();else{error.Text=Phone.Error;send.IsEnabled=true;}};
        window.Content=panel;window.ShowDialog();
    }
    private void BuildTools()
    {
        var actions=new WrapPanel();
        actions.Children.Add(Button("Ring phone (15 s)",async()=>await Phone.ToolAsync("tools.find",new{enabled=true})));
        actions.Children.Add(Button("Stop ring",async()=>await Phone.ToolAsync("tools.find",new{enabled=false})));
        actions.Children.Add(Button("Vibrate",async()=>await Phone.ToolAsync("tools.vibrate")));
        actions.Children.Add(Button("Display off",_c.Commands.SleepAsync));actions.Children.Add(Button("Wake display",_c.Commands.WakeAsync));
        actions.Children.Add(Button("Restart phone…",async()=>{if(MessageBox.Show(Window.GetWindow(this),"Restart the phone? Active calls and transfers will stop. PixelBridge reconnects after Android starts and is unlocked.","Restart phone",MessageBoxButton.OKCancel,MessageBoxImage.Question)==MessageBoxResult.OK)await _c.Commands.RestartAsync();}));_root.Children.Add(actions);
        var media=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(0,20,0,16)};_flash.Content="Flashlight";_flash.Margin=new(0,0,24,0);_flash.Click+=async(_,_)=>await Phone.ToolAsync("tools.flashlight",new{enabled=_flash.IsChecked==true});media.Children.Add(_flash);
        media.Children.Add(new TextBlock{Text="Media volume",VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,12,0)});_volume.Minimum=0;_volume.Maximum=15;_volume.Width=220;_volume.IsSnapToTickEnabled=true;_volume.TickFrequency=1;media.Children.Add(_volume);_root.Children.Add(media);
        _root.Children.Add(new TextBlock{Text="Apps",FontSize=18,FontWeight=FontWeights.SemiBold});_root.Children.Add(Prepare(_apps,160));_root.Children.Add(Button("Open selected app",async()=>{if(_apps.SelectedItem is Row{Value:PhoneApp app})await _c.Commands.LaunchAsync(app.Component);}));
        _root.Children.Add(new TextBlock{Text="Phone downloads",FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new(0,22,0,12)});_root.Children.Add(Prepare(_files,190));
        var files=new WrapPanel();files.Children.Add(Button("Refresh files",RefreshFiles));
        files.Children.Add(Button("Send file…",async()=>{var dialog=new OpenFileDialog{Multiselect=false};if(dialog.ShowDialog(Window.GetWindow(this))==true){_detail.Text="Sending file…";await _c.Commands.UploadAsync(dialog.FileName);_detail.Text="Sent to phone Downloads";await RefreshFiles();}}));
        files.Children.Add(Button("Save selected file…",async()=>{if(_files.SelectedItem is Row row){var dialog=new SaveFileDialog{FileName=row.Title,OverwritePrompt=true};if(dialog.ShowDialog(Window.GetWindow(this))==true){_detail.Text="Receiving file…";await _c.Commands.DownloadAsync(row.Title,dialog.FileName);_detail.Text="File saved";}}}));_root.Children.Add(files);
        _detail.TextWrapping=TextWrapping.Wrap;_detail.Margin=new(0,12,0,0);_root.Children.Add(_detail);
    }
    private async Task RefreshFiles(){var names=await _c.Commands.DownloadsAsync();_files.ItemsSource=names.Select(n=>new Row(n,n,"Downloads",n)).ToArray();}
    public void SetActive(bool active)
    {
        if(_active==active)return;_active=active;
        if(active){_needsLoad=true;if(_c.Session.IsConnected)_=LoadAsync();}
        else{_searchTimer.Stop();_volumeTimer.Stop();_volumePending=false;if(_c.Session.IsConnected)_=Phone.WatchAsync(false);}
    }
    private async Task LoadAsync()
    {
        if(!_active||!_c.Session.IsConnected)return;_needsLoad=false;
        await Phone.WatchAsync(_mode is "messages" or "calls" or "contacts");await Phone.RefreshStatusAsync();
        if(_mode=="messages"){await Phone.RefreshThreadsAsync();if(Phone.SelectedThread is long id)await Phone.LoadMessagesAsync(id);}
        if(_mode=="contacts")await Phone.SearchContactsAsync(_search.Text);
        if(_mode=="calls")await Phone.RefreshHistoryAsync();
        if(_mode=="notifications")await Phone.RefreshNotificationsAsync();
        if(_mode=="tools"){await Phone.RefreshToolsAsync();await Phone.RefreshAppsAsync();try{await RefreshFiles();}catch(Exception e){_detail.Text=e.Message;}}
        Tick();
    }
    private void SetRows(ListBox list,IEnumerable<Row> rows)
    {
        var key=(list.SelectedItem as Row)?.Key;var values=rows.ToArray();_updating=true;list.ItemsSource=values;list.SelectedItem=values.FirstOrDefault(x=>x.Key==key);_updating=false;
    }
    private static IEnumerable<Button> Buttons(DependencyObject root) { foreach(var child in LogicalTreeHelper.GetChildren(root)) if(child is DependencyObject d) { if(d is Button b)yield return b; foreach(var nested in Buttons(d))yield return nested; } }
    public void Tick()
    {
        foreach(var button in Buttons(_root)) if(Equals(button.Tag,"phoneAction"))button.IsEnabled=_c.Session.IsConnected&&!_c.PrivacyMode;
        _flash.IsEnabled=_volume.IsEnabled=_c.Session.IsConnected&&!_c.PrivacyMode;
        if(_active&&_needsLoad&&_c.Session.IsConnected)_=LoadAsync();
        _notice.Text=!_c.Session.IsConnected?"Connect your phone over USB. Data loads when this view is open.":Phone.Error;
        _send.IsEnabled=_c.Session.IsConnected&&!Phone.Sending&&!string.IsNullOrWhiteSpace(_number.Text)&&!string.IsNullOrWhiteSpace(_draft.Text);_receipt.Text=Phone.SendStatus;
        if(_mode=="messages")
        {
            if(!ReferenceEquals(_rows,Phone.Threads)){_rows=Phone.Threads;SetRows(_list,Phone.Threads.Select(x=>new Row(x.Id.ToString(),x.Address,(x.Unread?"Unread · ":"")+x.Preview,x)));}
            if(!ReferenceEquals(_messageRows,Phone.Messages)){_messageRows=Phone.Messages;SetRows(_messages,Phone.Messages.Select(x=>new Row(x.Id.ToString(),x.Body,(x.Incoming?"Received":"Sent")+" · "+DateTimeOffset.FromUnixTimeMilliseconds(x.DateMs).LocalDateTime.ToString("t"),x)));if(_messages.Items.Count>0)_messages.ScrollIntoView(_messages.Items[^1]);}
        }
        if(_mode=="contacts"&&!ReferenceEquals(_rows,Phone.Contacts)){_rows=Phone.Contacts;SetRows(_list,Phone.Contacts.Select(x=>new Row(x.Id+":"+x.Number,x.Name,x.Number,x)));}
        if(_mode=="calls")
        {
            if(!ReferenceEquals(_rows,Phone.Calls)){_rows=Phone.Calls;SetRows(_list,Phone.Calls.Select(x=>new Row(x.Id,x.Number,x.State,x)));}
            if(!ReferenceEquals(_messageRows,Phone.History)){_messageRows=Phone.History;SetRows(_messages,Phone.History.Select(x=>new Row(x.Id.ToString(),x.Name.Length>0?x.Name:x.Number,x.Number+" · "+DateTimeOffset.FromUnixTimeMilliseconds(x.DateMs).LocalDateTime.ToString("g"),x)));}
            var call=SelectedCall();_answer.IsEnabled=Phone.Dialer&&call?.State=="ringing";_end.IsEnabled=Phone.Dialer&&call!=null;_hold.IsEnabled=Phone.Dialer&&call?.CanHold==true;_hold.Content=call?.State=="held"?"Resume":"Hold";_mute.IsEnabled=_end.IsEnabled;_mute.Content=Phone.Muted?"Unmute":"Mute";
            _updating=true;_route.SelectedItem=Phone.CallRoute;_route.IsEnabled=_end.IsEnabled;_updating=false;
        }
        if(_mode=="notifications"&&!ReferenceEquals(_notificationRows,Phone.Notifications)){_notificationRows=Phone.Notifications;SetRows(_list,Phone.Notifications.Select(x=>new Row(x.Key,x.App+(x.Sender.Length>0?" · "+x.Sender:""),x.Redacted?"Content hidden by phone":x.Text??"",x)));}
        if(_mode=="tools")
        {
            if(!ReferenceEquals(_appRows,Phone.Apps)){_appRows=Phone.Apps;SetRows(_apps,Phone.Apps.Select(x=>new Row(x.Component,x.Name,x.Component,x)));}
            if(Phone.Tools!=null){_updating=true;_flash.IsChecked=(bool?)Phone.Tools["flashlight"]==true;_volume.Maximum=(int?)Phone.Tools["maximumVolume"]??15;if(!_volumePending&&!_volume.IsMouseCaptureWithin)_volume.Value=(int?)Phone.Tools["volume"]??0;_updating=false;}
        }
    }
}
