using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PixelBridge.Core;

namespace PixelBridge.Linux;

public sealed class PhoneHubView : UserControl
{
    private readonly AppModel _model;
    private readonly string _mode;
    private readonly StackPanel _root=new(){Spacing=16};
    private readonly TextBlock _notice=new(){TextWrapping=TextWrapping.Wrap},_receipt=new(){TextWrapping=TextWrapping.Wrap},_detail=new(){TextWrapping=TextWrapping.Wrap};
    private readonly ListBox _list=new(),_messages=new(),_apps=new(),_files=new();
    private readonly TextBox _number=new(){PlaceholderText="Phone number"},_draft=new(){PlaceholderText="Write a message"},_search=new(){PlaceholderText="Search names or numbers"},_tone=new(){PlaceholderText="Digit",MaxLength=1,Width=65};
    private readonly Button _send=new(){Content="Send SMS"},_answer=new(){Content="Answer"},_end=new(){Content="End call"},_hold=new(){Content="Hold"},_mute=new(){Content="Mute"},_merge=new(){Content="Merge calls"},_swap=new(){Content="Swap calls"};
    private readonly ComboBox _route=new(), _account=new();
    private readonly Slider _volume=new(){Minimum=0,Maximum=15,Width=210,TickFrequency=1,IsSnapToTickEnabled=true};
    private readonly ToggleSwitch _flash=new(){Content="Flashlight"};
    private readonly CheckBox _computerAudio=new();
    private readonly TextBlock _callAudioStatus=new(){TextWrapping=TextWrapping.Wrap};
    private readonly Button _listeningMute=new(){Content="Mute speakers"};
    private readonly DispatcherTimer _searchTimer=new(){Interval=TimeSpan.FromMilliseconds(280)};
    private readonly DispatcherTimer _volumeTimer=new(){Interval=TimeSpan.FromMilliseconds(220)};
    private bool _volumePending;
    private bool _active,_loading=true,_needsLoad=true,_working;
    private object? _rows,_messageRows,_appRows,_notificationRows,_accountRows,_endpointRows;
    private PhoneWorkspace Phone=>_model.Phone;
    private sealed record Row(string Key,string Title,string Detail,object Value);
    public PhoneHubView(AppModel model,string mode)
    {
        _model=model;_mode=mode;
        _root.Children.Add(new TextBlock{Text=mode switch{"messages"=>"Messages","calls"=>"Calls","contacts"=>"Contacts","notifications"=>"Notifications",_=>"Phone tools"},FontSize=28,FontWeight=FontWeight.SemiBold});
        _root.Children.Add(_notice);
        var setup=new WrapPanel{Orientation=Orientation.Horizontal};setup.Children.Add(Action("Set up on phone…",async()=>{await _model.Commands.OpenSetupAsync(_mode=="notifications"?"notifications":"communications");_model.LaunchScrcpy();}));setup.Children.Add(Action("Show phone screen",()=>{_model.LaunchScrcpy();return Task.CompletedTask;}));setup.Children.Add(Action("Refresh",LoadAsync));_root.Children.Add(setup);
        _volumeTimer.Tick+=async(_,_)=>{_volumeTimer.Stop();await Phone.ToolAsync("tools.volume",new{value=(int)_volume.Value});_volumePending=false;};
        _volume.PropertyChanged+=(_,e)=>{if(e.Property==Slider.ValueProperty&&!_loading&&_active&&_model.Session.IsConnected){_volumePending=true;_volumeTimer.Stop();_volumeTimer.Start();}};
        _searchTimer.Tick+=async(_,_)=>{_searchTimer.Stop();await Phone.SearchContactsAsync(_search.Text??"");Tick();};
        if(mode=="messages")BuildMessages();else if(mode=="calls")BuildCalls();else if(mode=="contacts")BuildContacts();else if(mode=="notifications")BuildNotifications();else BuildTools();
        _root.Children.Add(_detail);Content=_root;_loading=false;
        Phone.PhoneDataChanged+=()=>Dispatcher.UIThread.Post(()=>{if(_active)_=LoadAsync();});
        _model.Session.StateChanged+=(state,detail)=>{if(state==LinkState.Connected)_needsLoad=true;};
    }
    private Button Action(string text,Func<Task> action)
    {
        var button=new Button{Content=text,Margin=new(0,0,8,8),Tag="phoneAction"};button.Click+=async(_,_)=>await Run(action);return button;
    }
    private async Task Run(Func<Task> action)
    {
        if(_working)return;if(!_model.Session.IsConnected){_detail.Text="Connect the phone first.";return;}_working=true;
        try{await action();}catch(Exception e){_detail.Text=e.Message;}finally{_working=false;Tick();}
    }
    private static TextBlock Hint(string value)=>new(){Text=value,TextWrapping=TextWrapping.Wrap,Classes={"secondary"}};
    private readonly Dictionary<ListBox, TextBlock> _empty = new();
    private Control Prepare(ListBox list,double height)
    {
        list.Height=height;list.BorderThickness=new(0);
        list.ItemTemplate=new FuncDataTemplate<Row>((row,scope)=>new StackPanel{Spacing=5,Margin=new(6,7),Children={new TextBlock{Text=row?.Title,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap},new TextBlock{Text=row?.Detail,Classes={"secondary"},FontSize=12,TextWrapping=TextWrapping.Wrap}}});
        var grid = new Grid(); grid.Children.Add(list);
        var empty = new TextBlock { Text=EmptyLabel(list), TextWrapping=TextWrapping.Wrap, TextAlignment=TextAlignment.Center, HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, MaxWidth=280, Margin=new(22), Classes={"secondary"} };
        _empty[list]=empty; grid.Children.Add(empty);
        return new Border { Classes={"card"}, Padding=new(0), Margin=new(0), Child=grid };
    }
    private string EmptyLabel(ListBox list) => list==_apps ? "Your phone’s apps appear here." : list==_files ? "Files from the phone’s Downloads folder appear here." : list==_messages ? (_mode=="calls" ? "Your recent calls appear here after call-history access is enabled." : "Choose a conversation, or enter a number to start a new message.") : _mode switch { "messages"=>"Your recent SMS conversations appear here.", "calls"=>"No active call. You’re ready when the phone rings.", "contacts"=>"Search for someone by name or phone number.", _=>"Your phone’s notifications appear here after access is enabled." };
    private void BuildMessages()
    {
        _root.Children.Add(Action("New message",()=>{Phone.BeginNewMessage();_number.Text="";_draft.Text="";_list.SelectedIndex=-1;return Task.CompletedTask;}));
        var grid=new Grid{ColumnDefinitions=new("230,20,*")};grid.Children.Add(Prepare(_list,470));
        _list.SelectionChanged+=async(_,_)=>{if(_loading||_list.SelectedItem is not Row{Value:TextThread thread})return;_number.Text=thread.Address;await Phone.LoadMessagesAsync(thread.Id);Tick();};
        var conversation=new StackPanel{Spacing=12};Grid.SetColumn(conversation,2);grid.Children.Add(conversation);conversation.Children.Add(_number);conversation.Children.Add(Prepare(_messages,300));_draft.AcceptsReturn=true;_draft.TextWrapping=TextWrapping.Wrap;_draft.Height=85;_draft.MaxLength=12000;conversation.Children.Add(_draft);
        _send.HorizontalAlignment=HorizontalAlignment.Right;_send.Classes.Add("accent");_send.Click+=async(_,_)=>{if(await Phone.SendTextAsync(_number.Text??"",_draft.Text??"")){_draft.Text="";await Phone.RefreshThreadsAsync();if(Phone.SelectedThread is long id)await Phone.LoadMessagesAsync(id);}Tick();};conversation.Children.Add(_send);conversation.Children.Add(_receipt);_root.Children.Add(grid);
        _root.Children.Add(Hint("SMS uses your phone’s carrier plan. RCS and other messaging apps can be replied to from Notifications when Android offers a reply action."));
    }
    private void BuildCalls()
    {
        _root.Children.Add(Action("Enable incoming-call controls…",async()=>{await _model.Commands.OpenSetupAsync("calling");_model.LaunchScrcpy();}));
        var dial=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};_number.Width=320;_number.FontSize=18;dial.Children.Add(_number);dial.Children.Add(Action("Call",async()=>await Phone.CallAsync(_number.Text??"",(_account.SelectedItem as PhoneAccount)?.Id)));_root.Children.Add(dial);
        _account.MinWidth=240;_account.PlaceholderText="Choose SIM / phone line";_account.HorizontalAlignment=HorizontalAlignment.Left;_root.Children.Add(_account);
        _computerAudio.Content="Use computer microphone and speakers";_computerAudio.IsCheckedChanged+=(_,_)=>{if(_loading)return;_model.Settings.UseComputerCallAudio=_computerAudio.IsChecked==true;_model.Settings.Save();_model.CallAudio.SetEnabled(_computerAudio.IsChecked==true);};_root.Children.Add(_computerAudio);
        _root.Children.Add(_callAudioStatus);_listeningMute.Click+=(_,_)=>_model.CallAudio.ToggleListeningMute();_listeningMute.HorizontalAlignment=HorizontalAlignment.Left;_root.Children.Add(_listeningMute);
        _root.Children.Add(Prepare(_list,140));var actions=new WrapPanel();
        foreach(var b in new[]{_answer,_end,_hold,_mute,_merge,_swap}){b.Margin=new(0,0,8,8);actions.Children.Add(b);}
        _answer.Click+=async(_,_)=>await Phone.CallActionAsync("phone.answer",SelectedCall()?.Id);_end.Click+=async(_,_)=>await Phone.CallActionAsync("phone.end",SelectedCall()?.Id);
        _hold.Click+=async(_,_)=>await Phone.CallActionAsync(SelectedCall()?.State=="held"?"phone.unhold":"phone.hold",SelectedCall()?.Id);_mute.Click+=async(_,_)=>await Phone.CallActionAsync("phone.mute",args:new{enabled=!Phone.Muted});
        _merge.Click+=async(_,_)=>await Phone.CallActionAsync("phone.merge",SelectedCall()?.Id);_swap.Click+=async(_,_)=>await Phone.CallActionAsync("phone.swap",SelectedCall()?.Id);
        _route.MinWidth=140;_route.Margin=new(0,0,8,8);actions.Children.Add(_route);_route.SelectionChanged+=async(_,_)=>{if(!_loading&&Phone.Calls.Count>0&&_route.SelectedItem is PhoneEndpoint ep)await Phone.CallActionAsync("phone.route",args:new{endpointId=ep.Id,route=ep.Route});};
        actions.Children.Add(Action("Silence ring",()=>{Phone.SilenceRinging();return Task.CompletedTask;}));_root.Children.Add(actions);
        _root.Children.Add(Hint("Use the keypad to dial. During a connected call, it sends tones for menus and extensions."));
        var keypad=new Avalonia.Controls.Primitives.UniformGrid {Columns=3,Width=240,HorizontalAlignment=HorizontalAlignment.Left};
        foreach(var digit in "123456789*0#") {
            string key=digit.ToString();
            var button=Action(key,async()=>{if(SelectedCall() is {State:"active"} call)await Phone.CallActionAsync("phone.dtmf",call.Id,new{digit=key});else _number.Text+=key;});
            button.FontSize=22;button.MinHeight=48;button.Margin=new(0,0,8,8);keypad.Children.Add(button);
        }
        _root.Children.Add(keypad);
        _root.Children.Add(new TextBlock{Text="Recent calls",FontSize=18,FontWeight=FontWeight.SemiBold});_root.Children.Add(Prepare(_messages,280));_messages.SelectionChanged+=(_,_)=>{if(_messages.SelectedItem is Row{Value:RecentCall row})_number.Text=row.Number;};
    }
    private PhoneCall? SelectedCall()=>(_list.SelectedItem as Row)?.Value as PhoneCall??Phone.Calls.FirstOrDefault();
    private void BuildContacts()
    {
        _search.TextChanged+=(_,_)=>{if(_active){_searchTimer.Stop();_searchTimer.Start();}};_root.Children.Add(_search);_root.Children.Add(Prepare(_list,440));var actions=new WrapPanel();
        actions.Children.Add(Action("Call",async()=>{if(_list.SelectedItem is Row{Value:PhoneContact person})await Phone.CallAsync(person.Number);}));
        actions.Children.Add(Action("Message…",async()=>{if(_list.SelectedItem is Row{Value:PhoneContact person})await Compose(person.Number,person.Name,null);}));_root.Children.Add(actions);
    }
    private void BuildNotifications()
    {
        var settings=new WrapPanel();var enabled=new CheckBox{Content="Enable notifications",IsChecked=_model.Settings.NotificationsEnabled,Margin=new(0,0,20,8)};var previews=new CheckBox{Content="Show previews and replies",IsChecked=_model.Settings.NotificationShowContent};
        async void Change(object? sender,Avalonia.Interactivity.RoutedEventArgs e){_model.Settings.NotificationsEnabled=enabled.IsChecked==true;_model.Settings.NotificationShowContent=previews.IsChecked==true;_model.Settings.Save();Phone.ConfigureNotifications(_model.Settings.NotificationsEnabled,_model.Settings.NotificationShowContent);if(_model.Settings.NotificationsEnabled)await Phone.RefreshNotificationsAsync();Tick();}
        enabled.Click+=Change;previews.Click+=Change;settings.Children.Add(enabled);settings.Children.Add(previews);_root.Children.Add(settings);
        _root.Children.Add(Hint("Enable notification access and message previews on the phone to read and reply. Replies appear only when the source app provides Android's text-reply action."));_root.Children.Add(Prepare(_list,430));var actions=new WrapPanel();
        actions.Children.Add(Action("Reply…",async()=>{if(_list.SelectedItem is Row{Value:PhoneNotification n}&&n.CanReply)await Compose("",n.App,n.Key);}));actions.Children.Add(Action("Dismiss",async()=>{if(_list.SelectedItem is Row{Value:PhoneNotification n})await Phone.DismissAsync(n.Key);}));_root.Children.Add(actions);
    }
    private async Task Compose(string number,string title,string? replyKey)
    {
        Phone.BeginNewMessage();var window=new Window{Title=replyKey==null?"Message "+title:"Reply in "+title,Width=470,Height=350,WindowStartupLocation=WindowStartupLocation.CenterOwner,CanResize=false};
        var panel=new StackPanel{Margin=new(24),Spacing=14};panel.Children.Add(new TextBlock{Text=title,FontSize=22,FontWeight=FontWeight.SemiBold});panel.Children.Add(Hint(number));var text=new TextBox{AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=130,MaxLength=replyKey==null?12000:4000};panel.Children.Add(text);var error=Hint("");panel.Children.Add(error);var send=new Button{Content=replyKey==null?"Send SMS":"Send reply",HorizontalAlignment=HorizontalAlignment.Right,Classes={"accent"}};panel.Children.Add(send);
        send.Click+=async(_,_)=>{send.IsEnabled=false;bool ok=replyKey==null?await Phone.SendTextAsync(number,text.Text??""):await Phone.ReplyAsync(replyKey,text.Text??"");if(ok)window.Close();else{error.Text=Phone.Error;send.IsEnabled=true;}};window.Content=panel;
        if(TopLevel.GetTopLevel(this) is Window owner)await window.ShowDialog(owner);
    }
    private void BuildTools()
    {
        var actions=new WrapPanel();actions.Children.Add(Action("Ring phone (15 s)",async()=>await Phone.ToolAsync("tools.find",new{enabled=true})));actions.Children.Add(Action("Stop ring",async()=>await Phone.ToolAsync("tools.find",new{enabled=false})));actions.Children.Add(Action("Vibrate",async()=>await Phone.ToolAsync("tools.vibrate")));actions.Children.Add(Action("Display off",_model.Commands.SleepAsync));actions.Children.Add(Action("Wake display",_model.Commands.WakeAsync));actions.Children.Add(Action("Restart phone…",async()=>{if(await ConfirmRestart())await _model.Commands.RestartAsync();}));_root.Children.Add(actions);
        var media=new StackPanel{Orientation=Orientation.Horizontal,Spacing=22};_flash.Click+=async(_,_)=>{if(!_loading)await Phone.ToolAsync("tools.flashlight",new{enabled=_flash.IsChecked==true});};media.Children.Add(_flash);media.Children.Add(new TextBlock{Text="Media volume",VerticalAlignment=VerticalAlignment.Center});media.Children.Add(_volume);_root.Children.Add(media);
        var columns = new Grid { ColumnDefinitions=new("*,20,*") };
        var apps = new StackPanel { Spacing=12 }; var downloads = new StackPanel { Spacing=12 };
        Grid.SetColumn(downloads,2); columns.Children.Add(apps); columns.Children.Add(downloads); _root.Children.Add(columns);
        apps.Children.Add(new TextBlock{Text="Apps",FontSize=18,FontWeight=FontWeight.SemiBold});apps.Children.Add(Prepare(_apps,220));apps.Children.Add(Action("Open selected app",async()=>{if(_apps.SelectedItem is Row{Value:PhoneApp app})await _model.Commands.LaunchAsync(app.Component);}));
        downloads.Children.Add(new TextBlock{Text="Phone downloads",FontSize=18,FontWeight=FontWeight.SemiBold});downloads.Children.Add(Prepare(_files,220));var files=new WrapPanel();files.Children.Add(Action("Refresh files",RefreshFiles));
        files.Children.Add(Action("Send file…",async()=>{var top=TopLevel.GetTopLevel(this);if(top==null)return;var picked=await top.StorageProvider.OpenFilePickerAsync(new(){AllowMultiple=false,Title="Send to phone Downloads"});var path=picked.FirstOrDefault()?.TryGetLocalPath();if(path!=null){_detail.Text="Sending file…";await _model.Commands.UploadAsync(path);_detail.Text="Sent to phone Downloads";await RefreshFiles();}}));
        files.Children.Add(Action("Save selected file…",async()=>{if(_files.SelectedItem is not Row row)return;var top=TopLevel.GetTopLevel(this);if(top==null)return;var picked=await top.StorageProvider.SaveFilePickerAsync(new(){SuggestedFileName=row.Title,Title="Save phone download"});var path=picked?.TryGetLocalPath();if(path!=null){_detail.Text="Receiving file…";await _model.Commands.DownloadAsync(row.Title,path);_detail.Text="File saved";}}));downloads.Children.Add(files);
    }
    private async Task<bool> ConfirmRestart()
    {
        var window=new Window{Title="Restart phone",Width=430,Height=220,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};var panel=new StackPanel{Margin=new(24),Spacing=18};panel.Children.Add(Hint("Restart the connected phone? Active calls and transfers will stop. PixelBridge reconnects after Android starts and is unlocked."));var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12,HorizontalAlignment=HorizontalAlignment.Right};var cancel=new Button{Content="Cancel"};cancel.Click+=(_,_)=>window.Close(false);var confirm=new Button{Content="Restart phone",Classes={"accent"}};confirm.Click+=(_,_)=>window.Close(true);buttons.Children.Add(cancel);buttons.Children.Add(confirm);panel.Children.Add(buttons);window.Content=panel;return TopLevel.GetTopLevel(this) is Window owner&&await window.ShowDialog<bool>(owner);
    }
    private async Task RefreshFiles(){var names=await _model.Commands.DownloadsAsync();_files.ItemsSource=names.Select(n=>new Row(n,n,"Downloads",n)).ToArray();}
    public void SetActive(bool active){if(_active==active)return;_active=active;if(active){_needsLoad=true;if(_model.Session.IsConnected)_=LoadAsync();}else{_searchTimer.Stop();_volumeTimer.Stop();_volumePending=false;if(_model.Session.IsConnected)_=Phone.WatchAsync(false);}}
    private async Task LoadAsync()
    {
        if(!_active||!_model.Session.IsConnected)return;_needsLoad=false;await Phone.WatchAsync(_mode is "messages" or "calls" or "contacts");await Phone.RefreshStatusAsync();
        if(_mode=="messages"){await Phone.RefreshThreadsAsync();if(Phone.SelectedThread is long id)await Phone.LoadMessagesAsync(id);}if(_mode=="contacts")await Phone.SearchContactsAsync(_search.Text??"");if(_mode=="calls")await Phone.RefreshHistoryAsync();if(_mode=="notifications")await Phone.RefreshNotificationsAsync();if(_mode=="tools"){await Phone.RefreshToolsAsync();await Phone.RefreshAppsAsync();try{await RefreshFiles();}catch(Exception e){_detail.Text=e.Message;}}Tick();
    }
    private void SetRows(ListBox list,IEnumerable<Row> rows){var key=(list.SelectedItem as Row)?.Key;var values=rows.ToArray();_loading=true;list.ItemsSource=values;list.SelectedItem=values.FirstOrDefault(x=>x.Key==key);_loading=false;}
    public void Tick()
    {
        foreach(var pair in _empty) pair.Value.IsVisible=pair.Key.ItemCount==0;
        foreach(var button in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(this).OfType<Button>())if(Equals(button.Tag,"phoneAction"))button.IsEnabled=_model.Session.IsConnected&&!_model.Privacy;
        _flash.IsEnabled=_volume.IsEnabled=_model.Session.IsConnected&&!_model.Privacy;
        if(_active&&_needsLoad&&_model.Session.IsConnected)_=LoadAsync();_notice.Text=!_model.Session.IsConnected?"Connect your phone over USB. Data loads when this view is open.":Phone.Error;
        _send.IsEnabled=_model.Session.IsConnected&&!_model.Privacy&&!Phone.Sending&&!string.IsNullOrWhiteSpace(_number.Text)&&!string.IsNullOrWhiteSpace(_draft.Text);_receipt.Text=Phone.SendStatus;
        if(_mode=="messages"){if(!ReferenceEquals(_rows,Phone.Threads)){_rows=Phone.Threads;SetRows(_list,Phone.Threads.Select(x=>new Row(x.Id.ToString(),x.Address,(x.Unread?"Unread · ":"")+x.Preview,x)));}if(!ReferenceEquals(_messageRows,Phone.Messages)){_messageRows=Phone.Messages;SetRows(_messages,Phone.Messages.Select(x=>new Row(x.Id.ToString(),x.Body,(x.Incoming?"Received":"Sent")+" · "+DateTimeOffset.FromUnixTimeMilliseconds(x.DateMs).LocalDateTime.ToString("t"),x)));if(_messages.ItemCount>0)_messages.ScrollIntoView(_messages.ItemCount-1);}}
        if(_mode=="contacts"&&!ReferenceEquals(_rows,Phone.Contacts)){_rows=Phone.Contacts;SetRows(_list,Phone.Contacts.Select(x=>new Row(x.Id+":"+x.Number,x.Name,x.Number,x)));}
        if(_mode=="calls"){if(!ReferenceEquals(_rows,Phone.Calls)){_rows=Phone.Calls;SetRows(_list,Phone.Calls.Select(x=>new Row(x.Id,x.Number,x.State,x)));}if(!ReferenceEquals(_messageRows,Phone.History)){_messageRows=Phone.History;SetRows(_messages,Phone.History.Select(x=>new Row(x.Id.ToString(),x.Name.Length>0?x.Name:x.Number,x.Number+" · "+DateTimeOffset.FromUnixTimeMilliseconds(x.DateMs).LocalDateTime.ToString("g"),x)));}_loading=true;_computerAudio.IsChecked=_model.CallAudio.Enabled;_loading=false;_computerAudio.IsEnabled=!_model.Privacy;_callAudioStatus.Text=_model.CallAudio.Status;_listeningMute.IsVisible=_model.CallAudio.Active;_listeningMute.Content=_model.CallAudio.ListeningMuted?"Unmute speakers":"Mute speakers";var call=SelectedCall();_answer.IsEnabled=Phone.Dialer&&call?.State=="ringing";_end.IsEnabled=Phone.Dialer&&call!=null;_hold.IsEnabled=Phone.Dialer&&call?.CanHold==true;_hold.Content=call?.State=="held"?"Resume":"Hold";_mute.IsEnabled=_end.IsEnabled;_mute.Content=Phone.Muted?"Unmute microphone":"Mute microphone";_merge.IsEnabled=Phone.Dialer&&call?.CanMerge==true;_swap.IsEnabled=Phone.Dialer&&call?.CanSwap==true;_loading=true;if(!ReferenceEquals(_endpointRows,Phone.Endpoints)){_endpointRows=Phone.Endpoints;_route.ItemsSource=Phone.Endpoints;}_route.SelectedItem=Phone.Endpoints.FirstOrDefault(x=>x.Selected)??Phone.Endpoints.FirstOrDefault(x=>x.Route==Phone.CallRoute);_route.IsEnabled=_end.IsEnabled;if(!ReferenceEquals(_accountRows,Phone.Accounts)){var chosen=(_account.SelectedItem as PhoneAccount)?.Id;_accountRows=Phone.Accounts;_account.ItemsSource=Phone.Accounts;_account.SelectedItem=Phone.Accounts.FirstOrDefault(x=>x.Id==chosen)??Phone.Accounts.FirstOrDefault(x=>x.Default)??(Phone.Accounts.Count==1?Phone.Accounts[0]:null);}_account.IsVisible=Phone.Accounts.Count>1;_account.IsEnabled=_model.Session.IsConnected&&!_model.Privacy;_end.Content=call?.State=="ringing"?"Decline":"End call";_loading=false;}
        if(_mode=="notifications"&&!ReferenceEquals(_notificationRows,Phone.Notifications)){_notificationRows=Phone.Notifications;SetRows(_list,Phone.Notifications.Select(x=>new Row(x.Key,x.App+(x.Sender.Length>0?" · "+x.Sender:""),x.Redacted?"Content hidden by phone":x.Text??"",x)));}
        if(_mode=="tools"){if(!ReferenceEquals(_appRows,Phone.Apps)){_appRows=Phone.Apps;SetRows(_apps,Phone.Apps.Select(x=>new Row(x.Component,x.Name,"Installed on your phone",x)));}if(Phone.Tools!=null){_loading=true;_flash.IsChecked=(bool?)Phone.Tools["flashlight"]==true;_volume.Maximum=(int?)Phone.Tools["maximumVolume"]??15;if(!_volumePending&&!_volume.IsPointerOver)_volume.Value=(int?)Phone.Tools["volume"]??0;_loading=false;}}
    }
}

public sealed class CompassHubView : UserControl
{
    private readonly AppModel _model;private readonly TextBlock _heading=new(){FontSize=42,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap},_detail=new(){TextWrapping=TextWrapping.Wrap};private readonly CompassRose _rose=new(){Width=320,Height=320};
    public CompassHubView(AppModel model)
    {
        _model=model;var root=new StackPanel{Spacing=24};root.Children.Add(new TextBlock{Text="Compass",FontSize=28,FontWeight=FontWeight.SemiBold});var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=32};row.Children.Add(_rose);var values=new StackPanel{Spacing=16,VerticalAlignment=VerticalAlignment.Center,MaxWidth=320};values.Children.Add(_heading);values.Children.Add(new TextBlock{Text="Magnetic north",FontSize=18});values.Children.Add(_detail);row.Children.Add(values);root.Children.Add(row);root.Children.Add(new TextBlock{Text="Hold the phone flat, with its top edge pointing forward. Keep it away from magnets, speakers and metal surfaces. Readings pause when this view is hidden.",Classes={"secondary"},TextWrapping=TextWrapping.Wrap,MaxWidth=720,HorizontalAlignment=HorizontalAlignment.Left});Content=root;
    }
    public void Tick(){var r=_model.Compass.Reading;_heading.Text=r==null?(_model.Session.IsConnected?"Finding north…":"Connect your phone"):$"{r.Heading:0}° {r.Direction}";_detail.Text=r==null?"Connect your phone to use its rotation and magnetic sensors.":$"{r.Quality}\n\nPitch {r.Pitch:0.0}°    Roll {r.Roll:0.0}°"+(r.FieldMicrotesla is double f?$"\nMagnetic field {f:0.0} µT":"");_rose.Heading=r?.Heading??0;_rose.InvalidateVisual();}
}
sealed class CompassRose : Control
{
    public double Heading{get;set;}
    public override void Render(DrawingContext dc)
    {
        var center=new Point(Bounds.Width/2,Bounds.Height/2);double radius=Math.Min(Bounds.Width,Bounds.Height)/2-3;var accent=new SolidColorBrush(Color.FromRgb(30,130,105));var secondary=Brushes.Gray;var ink=ActualThemeVariant==Avalonia.Styling.ThemeVariant.Dark?Brushes.White:Brushes.Black;
        dc.DrawEllipse(null,new Pen(secondary,.7),center,radius,radius);
        for(int i=0;i<72;i++){double a=(i*5-Heading-90)*Math.PI/180;double size=i%6==0?17:8;dc.DrawLine(new Pen(i%18==0?accent:secondary,i%6==0?1.5:.7),new(center.X+Math.Cos(a)*(radius-13),center.Y+Math.Sin(a)*(radius-13)),new(center.X+Math.Cos(a)*(radius-13-size),center.Y+Math.Sin(a)*(radius-13-size)));}
        for(int i=0;i<4;i++){double a=(i*90-Heading-90)*Math.PI/180;var text=new FormattedText(new[]{"N","E","S","W"}[i],CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Inter"),17,i==0?accent:ink);dc.DrawText(text,new(center.X+Math.Cos(a)*(radius-48)-text.Width/2,center.Y+Math.Sin(a)*(radius-48)-text.Height/2));}
        for(int i=0;i<2;i++){double a=(-Heading+i*180)*Math.PI/180;Point Rotate(double x,double y)=>new(center.X+x*Math.Cos(a)-y*Math.Sin(a),center.Y+x*Math.Sin(a)+y*Math.Cos(a));var geometry=new StreamGeometry();using(var g=geometry.Open()){g.BeginFigure(Rotate(0,-radius*.53),true);g.LineTo(Rotate(12,0));g.LineTo(Rotate(-12,0));g.EndFigure(true);}dc.DrawGeometry(i==0?accent:secondary,null,geometry);}dc.DrawEllipse(ink,new Pen(accent,2),center,6,6);
    }
}
