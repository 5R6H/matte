using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path=System.Windows.Shapes.Path;
using System.Windows.Shell;
using SD=System.Drawing;
using WF=System.Windows.Forms;

// The tray panel: one frosted glass window (its backdrop is drawn by Backdrop.cs) with two pages, the menu and "换一个颜色", that slide into
// each other. The panel is created once and hidden/shown, so it opens instantly. Layout lives in GlassPanel.xaml.
partial class App {
 Window panel;DateTime autoHidden;bool holdSlide;
 System.Windows.Threading.DispatcherTimer panelTick;
 T P<T>(string name) where T:class{return panel.FindName(name) as T;}

 // ------------------------------------------------------------ window
 [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h,int a,ref int v,int s);
 [StructLayout(LayoutKind.Sequential)] struct MARGINS{public int l,r,t,b;}
 [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h,ref MARGINS m);
 [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int hgt,uint flags);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out RECT r);
 [StructLayout(LayoutKind.Sequential)] struct RECT{public int Left,Top,Right,Bottom;}
 [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

 // The panel's slow fades (the 1.6 s backdrop cross-fade, colour changes) run at 60 fps instead of the screen's 240:
 // they look the same, and the GPU stays free for the Spatial wallpaper, which follows the pointer behind the panel.
 // The quick, hand-driven motion (page slides, opening, presses: Animate below) runs at the screen's full rate, where
 // 60 fps would feel stepped.
 static bool frameRateSet;const int UiFrameRate=240;
 void CreatePanel(){
  if(!frameRateSet){frameRateSet=true;Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline),new FrameworkPropertyMetadata{DefaultValue=60});}
  using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("GlassPanel.xaml"))panel=(Window)XamlReader.Load(s);
  WindowChrome.SetWindowChrome(panel,new WindowChrome{CaptionHeight=0,GlassFrameThickness=new Thickness(-1),ResizeBorderThickness=new Thickness(0),CornerRadius=new CornerRadius(0)});
  panel.SourceInitialized+=delegate{
   var h=new WindowInteropHelper(panel).Handle;HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor=Colors.Transparent;
   var m=new MARGINS{l=-1,r=-1,t=-1,b=-1};DwmExtendFrameIntoClientArea(h,ref m);
   int v=styledLight==true?0:1;DwmSetWindowAttribute(h,20,ref v,4);   // dark frame, light in the simple style on a light system theme
   v=1;DwmSetWindowAttribute(h,38,ref v,4);       // no system backdrop (it lags over the desktop); see Backdrop.cs

   v=2;DwmSetWindowAttribute(h,33,ref v,4);       // rounded corners
  };
  // The panel sits on the taskbar: when its content changes height while open (the empty state, say),
  // its bottom stays where it is and it grows or shrinks upward, never under the taskbar.
  panel.SizeChanged+=(s,e)=>{if(panel.IsVisible&&e.HeightChanged&&e.PreviousSize.Height>0)panel.Top+=e.PreviousSize.Height-e.NewSize.Height;};
  // Hidden after the deactivation message returns: Explorer calls (restoring the colour) are refused while Windows is still delivering it.
  panel.Deactivated+=delegate{if(!dropping)panel.Dispatcher.BeginInvoke(new Action(()=>{if(!dropping&&!panel.IsActive)AutoHide();}));};
  // While a screenshot is being taken (Win+Shift+S) the panel stays; once focus moves anywhere else, it closes as usual.
  snipWatch=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(300)};snipWatch.Tick+=delegate{if(!PanelOpen||panel.IsActive){snipWatch.Stop();return;}if(!Snipping())AutoHide();};
  panel.PreviewKeyDown+=(s,e)=>{if(e.Key==System.Windows.Input.Key.Escape){if(page=="spatialfiles"||page=="spatialstore")GoPage("spatial",-1);else if(page=="sharp")LeaveSharp();else if(page=="playlist"){GoPage("menu",-1);RefreshPanel();}else if(page=="settings"||page=="spatial"){GoPage("menu",-1);RefreshPanel();}else HidePanel();e.Handled=true;}};
  CreateBackdrop();
  // icons
  Icon("PrevIcon",IPrev);Icon("PlayIcon",IPause);Icon("NextIcon",INext);Icon("PrefsIcon",IGear);Icon("SpatialIcon",ICube);
  Click("OrderBtn",()=>{SetOrder(Sequential?"shuffle":"sequential");RefreshPanel();});
  Icon("ExitIcon",IExit);Icon("BatteryIcon",IBattery);Icon("StartupIcon",IPower);
  Icon("SettingsIcon",IContrast);Icon("SettingsBackIcon",IBack);Icon("SpatialBackIcon",IBack);Icon("PlaylistBackIcon",IBack);
  Icon("WallFolderIcon",IFolder);Icon("AddFolderIcon",IFolder);Icon("AddImagesIcon",IImage);
  // settings + playlist pages
  Click("PrefsRow",()=>{RefreshPrefs();GoPage("settings",1);});
  Click("BatteryRow",()=>{SetBatteryFlat(!flatOnBattery);RefreshPrefs();});
  Click("StartupRow",()=>{string why=SetStartup(!StartupOn);RefreshPrefs();if(why!=null)Log("Startup: "+why);});
  
  Click("SettingsBack",()=>GoPage("menu",-1));
  HookCard();
  Click("PlaylistBack",()=>{GoPage("menu",-1);RefreshPanel();});
  Click("TimerBtn",OpenIntervalPopup);
  Click("EmptyAddBtn",()=>{RefreshPlaylist(null);GoPage("playlist",1);});
  // the list is its own little window: while it is open the panel must not close for losing focus to it
  var popup=P<System.Windows.Controls.Primitives.Popup>("IntervalPopup");
  popup.Opened+=delegate{dropping=true;};
  popup.Closed+=delegate{dropping=false;panel.Dispatcher.BeginInvoke(new Action(()=>{if(PanelOpen&&!panel.IsActive)AutoHide();}));};
  foreach(Button b in P<System.Windows.Controls.Primitives.UniformGrid>("MotionSeg").Children){var tag=(string)b.Tag;b.Click+=delegate{try{SetMotion(tag=="mouse"?(settings.MotionMouse??"follow"):(settings.MotionAuto??"drift"));RefreshSettings();}catch(Exception e){Log("Motion: "+e.Message);}};}
  foreach(Button b in P<System.Windows.Controls.Primitives.UniformGrid>("MotionSubSeg").Children){var i=int.Parse((string)b.Tag);b.Click+=delegate{try{bool mouse=MotionIsMouse;SetMotion(mouse?(i==0?"follow":"reverse"):(i==0?"drift":"random"));RefreshSettings();}catch(Exception e){Log("Motion: "+e.Message);}};}
  foreach(Button b in P<System.Windows.Controls.Primitives.UniformGrid>("BackgroundSeg").Children){var tag=(string)b.Tag;b.Click+=delegate{try{SetBackgroundMode(tag);RefreshSettings();RefreshPanel();}catch(Exception e){Log("Wallpaper background: "+e.Message);}};}
  Icon("BackgroundIcon",IBackdrop);
  foreach(Button b in P<System.Windows.Controls.Primitives.UniformGrid>("StyleSeg").Children){var tag=(string)b.Tag;b.Click+=delegate{try{if(settings.PanelStyle==tag)return;CrossfadePanel(()=>{settings.PanelStyle=tag;SaveSettings();ApplyPanelStyle();RefreshSettings();});}catch(Exception e){Log("Panel style: "+e.Message);}};}
  Click("AddFolderBtn",()=>AddPlaylistItems(true));Click("AddImagesBtn",()=>AddPlaylistItems(false));
  // menu actions
  Click("PlayBtn",()=>{holdSlide=!holdSlide;if(!holdSlide&&DateTime.Now>nextSlide)nextSlide=DateTime.Now.AddSeconds(5);RefreshPanel();});
  Click("NextBtn",()=>{try{AdvanceSlide();}catch(Exception e){Log("Next wallpaper: "+e.Message);}RefreshPanel();});
  Click("PrevBtn",()=>{PreviousSlide();});
  // The Spatial row opens its page; its switch still turns Spatial on / off in one click (it takes the press itself).
  Click("SpatialRow",()=>{RefreshSpatial(true);GoPage("spatial",1);});
  var track=P<Border>("SwitchTrack");track.Cursor=System.Windows.Input.Cursors.Hand;track.ToolTip="开 / 关立体壁纸";
  track.PreviewMouseLeftButtonDown+=(s,e)=>{e.Handled=true;try{SetSpatialEnabled(!spatialEnabled);RefreshPanel();}catch(Exception ex){Log("Spatial switch: "+ex.Message);}};
  Click("SpatialBack",()=>{GoPage("menu",-1);RefreshPanel();});
  // Spatial 图片: its own page, from the summary row
  Icon("GenRowIcon",IImage);Icon("GenRowChevron",IChevron);Icon("SpatialFilesBackIcon",IBack);
  Click("GenRow",()=>{if(!GeneratorAvailable&&!Generating){OpenSharp("spatial");return;}GenNoteShow(null);RefreshGenList();GenChanged();GoPage("spatialfiles",1);});
  Click("SpatialFilesBack",()=>{GoPage("spatial",-1);GenChanged();});
  // 存储与清理
  Icon("StoreRowIcon",IFolder);Icon("StoreRowChevron",IChevron);Icon("SpatialStoreBackIcon",IBack);Icon("StoreFolderIcon",IFolder);Icon("StoreOldIcon",ITrash);Icon("StoreAutoIcon",ICheck);
  Click("StoreRow",()=>{RefreshStore();GoPage("spatialstore",1);});
  Click("SpatialStoreBack",()=>{GoPage("spatial",-1);});
  Click("StoreOpenBtn",()=>{try{string d=settings.SpatialStoreTarget??SpatialCacheDir;if(System.IO.Directory.Exists(d))System.Diagnostics.Process.Start("explorer.exe","\""+d+"\"");}catch(Exception e){Log("Open store: "+e.Message);}});
  Click("StoreSharpBtn",StoreSharpClicked);Icon("StoreSharpIcon",ICube);
  CreateSharpPage();
  Click("StoreMoveBtn",()=>MoveStore(false));Click("StoreBackBtn",()=>MoveStore(true));Click("StoreOldBtn",CleanOldStore);

  P<Border>("SpatialOnTrack").MouseLeftButtonUp+=(s,e)=>{try{SetSpatialEnabled(!spatialEnabled);RefreshSpatial(false);}catch(Exception ex){Log("Spatial page: "+ex.Message);}};
  
  Click("GenBtn",()=>{GenNoteShow(null);if(Generating)StopGeneration();else{string why=StartGeneration();if(why!=null)GenNoteShow(why);}RefreshGenList();GenChanged();});
  Click("GenOneBtn",()=>{GenNoteShow(null);string s=currentSourcePath;if(s==null||Generating)return;string why=StartGeneration(s);if(why!=null)GenNoteShow(why);RefreshGenList();GenChanged();});
  Click("ExitRow",()=>{HidePanel();ExitThread();});   // quits; the photo on show stays as the wallpaper
  // picker
  panelTick=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};panelTick.Tick+=delegate{UpdateCountdown();};
  // live preview at most every 40 ms: each step is a call into Explorer
 }
 System.Windows.Threading.DispatcherTimer snipWatch;
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 static bool Snipping(){
  try{uint pid;GetWindowThreadProcessId(GetForegroundWindow(),out pid);if(pid==0)return false;
   using(var p=System.Diagnostics.Process.GetProcessById((int)pid)){string n=p.ProcessName;return n.Equals("ScreenClippingHost",StringComparison.OrdinalIgnoreCase)||n.Equals("SnippingTool",StringComparison.OrdinalIgnoreCase);}}
  catch{return false;}
 }
 void AutoHide(){if(Snipping()){if(!snipWatch.IsEnabled)snipWatch.Start();return;}snipWatch.Stop();HidePanel();autoHidden=DateTime.Now;}
 void Click(string name,Action a){P<Button>(name).Click+=delegate{try{a();}catch(Exception e){Log("Panel "+name+": "+e.Message);}};}
 public bool PanelOpen{get{return panel!=null&&panel.IsVisible;}}

 // Tray click (or a second start of Matte): toggles the panel.
 void TogglePanel(bool full){try{TogglePanelCore(full);}catch(Exception e){Log("Panel open: "+e);if(panel!=null)panel.Hide();}}
 void TogglePanelCore(bool full){
  if(PanelOpen){HidePanel();return;}
  if((DateTime.Now-autoHidden).TotalMilliseconds<400)return;   // this click is what just closed it (focus left for the tray)
  if(panel==null)CreatePanel();
  LoadSpatialCatalog();page="menu";
  Settle(P<StackPanel>("MenuPage"),true);
  foreach(var p in new FrameworkElement[]{P<Grid>("SettingsPage"),P<Grid>("PlaylistPage"),P<Grid>("SpatialPage"),P<Grid>("SpatialFilesPage"),P<Grid>("SpatialStorePage"),P<Grid>("SharpPage")}){Settle(p,false);p.Visibility=Visibility.Collapsed;}
  ApplyPanelStyle();
  RefreshPanel();
  panel.Left=-20000;panel.Top=-20000;panel.Show();panel.UpdateLayout();
  var h=new WindowInteropHelper(panel).Handle;RECT r;GetWindowRect(h,out r);int w=r.Right-r.Left,ht=r.Bottom-r.Top;
  var area=WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;int gap=(int)Math.Round(10*w/320.0);
  SetAmbient(currentSourcePath==null?null:DisplayPath(currentSourcePath),0);
  // WPF keeps its own idea of the position and re-applies it on layout changes, so it is told too (in DIPs).
  double k=w/panel.ActualWidth;panel.Left=(area.Right-w-gap)/k;panel.Top=(area.Bottom-ht-gap)/k;
  SetWindowPos(h,IntPtr.Zero,area.Right-w-gap,area.Bottom-ht-gap,0,0,0x0001|0x0004);
  panel.Activate();SetForegroundWindow(h);
  RefreshCube();   // only now is the panel on screen (RefreshPanel ran before it was shown)
  // open: content rises and fades in
  var root=P<Grid>("Root");var t=new TranslateTransform(0,8);root.RenderTransform=t;
  Animate(root,UIElement.OpacityProperty,0,1,220);Animate(t,TranslateTransform.YProperty,8,0,320);
  panelTick.Start();
 }
 // ---- the Spatial row's cube turns while Spatial is on: a real wireframe cube (only the edges of faces turned to the
 // viewer), seen from a little above, turning about its upright axis. It turns in bursts: half a turn that starts
 // quickly and eases to almost still, again and again (3.2 s each). Switched off, it does not jump: it turns on to the
 // next resting position (the cube looks the same every quarter turn) and stops, the ICube drawing again. Runs only
 // while the menu page is on screen.
 System.Windows.Threading.DispatcherTimer cubeTimer;System.Diagnostics.Stopwatch cubeClock;double cubeAngle=Math.PI/4,settleFrom,settleTo;bool cubeSettling;
 const double CubeBurst=3.2;
 void RefreshCube(){
  bool show=PanelOpen&&page=="menu";
  if(cubeTimer==null){cubeTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(16)};cubeTimer.Tick+=delegate{CubeFrame();};}
  if(show&&spatialEnabled){if(!cubeTimer.IsEnabled||cubeSettling){cubeSettling=false;cubeClock=System.Diagnostics.Stopwatch.StartNew();cubeBase=cubeAngle;cubeTimer.Start();}}
  else if(cubeTimer.IsEnabled&&!cubeSettling){
   if(!show){cubeTimer.Stop();cubeAngle=Math.PI/4;Icon("SpatialIcon",ICube);return;}
   // to the next resting angle (45° + a quarter turn) ahead
   double q=Math.PI/2,rest=Math.PI/4+Math.Ceiling((cubeAngle-Math.PI/4)/q-1e-6)*q;
   if(rest-cubeAngle<.15)rest+=q;   // too close to stop smoothly: one more quarter
   settleFrom=cubeAngle;settleTo=rest;cubeSettling=true;cubeClock=System.Diagnostics.Stopwatch.StartNew();
  }
 }
 double cubeBase;
 void CubeFrame(){
  var host=P<ContentControl>("SpatialIcon");var vb=host.Content as Viewbox;var path=vb==null?null:((Canvas)vb.Child).Children[0] as Path;
  if(!(PanelOpen&&page=="menu")||path==null){cubeTimer.Stop();cubeSettling=false;cubeAngle=Math.PI/4;Icon("SpatialIcon",ICube);return;}
  double t=cubeClock.Elapsed.TotalSeconds;
  if(cubeSettling){
   double p=Math.Min(1,t/.9),e=1-Math.Pow(1-p,3);cubeAngle=settleFrom+(settleTo-settleFrom)*e;
   if(p>=1){cubeTimer.Stop();cubeSettling=false;cubeAngle=Math.PI/4;Icon("SpatialIcon",ICube);return;}
  }else{
   if(!spatialEnabled){RefreshCube();return;}
   // each burst: a quick start (the first tenth eases in), then a long ease out to almost still
   int n=(int)(t/CubeBurst);double p=t/CubeBurst-n,s=(p<.1?5*p*p:p-.05)/.95,e=1-Math.Pow(1-s,4);
   cubeAngle=cubeBase+Math.PI*(n+e);
  }
  path.Data=Geometry.Parse(CubePath(cubeAngle));
 }
 static string CubePath(double theta){
  const double s=5.657,sinP=.5625;double cosP=Math.Sqrt(1-sinP*sinP),ct=Math.Cos(theta),st=Math.Sin(theta);
  Func<double,double,double,double[]> view=(x,y,z)=>{double x1=x*ct+z*st,z1=-x*st+z*ct;return new[]{x1,y*cosP-z1*sinP,y*sinP+z1*cosP};};   // screen x, screen up, toward the viewer
  var v=new double[8][];for(int i=0;i<8;i++)v[i]=view((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1);
  // faces: the axis, its side, and their four corners
  var faces=new List<Tuple<double[],int[]>>();
  for(int axis=0;axis<3;axis++)for(int side=0;side<2;side++){var c=new List<int>();for(int i=0;i<8;i++)if(((i>>axis)&1)==side)c.Add(i);
   var n=new double[3];n[axis]=side==0?-1:1;faces.Add(Tuple.Create(view(n[0],n[1],n[2]),new[]{c[0],c[1],c[3],c[2]}));}
  var edges=new HashSet<long>();
  foreach(var f in faces)if(f.Item1[2]>1e-6)for(int k=0;k<4;k++){int a=f.Item2[k],b=f.Item2[(k+1)%4];edges.Add(Math.Min(a,b)*8+Math.Max(a,b));}
  var sb=new System.Text.StringBuilder();var ci=System.Globalization.CultureInfo.InvariantCulture;
  foreach(long e in edges){var a=v[e/8];var b=v[e%8];sb.AppendFormat(ci,"M{0:0.##} {1:0.##}L{2:0.##} {3:0.##} ",12+s*a[0],12-s*a[1],12+s*b[0],12-s*b[1]);}
  return sb.ToString();
 }
 // A change of the whole panel's look (面板外观): a picture of the panel as it is lies over it while the change is made
 // underneath, then fades away, so the two looks dissolve into each other instead of switching.
 void CrossfadePanel(Action change){
  var root=P<Grid>("Root");Image shot=null;
  try{var dpi=VisualTreeHelper.GetDpi(root);int w=(int)Math.Ceiling(root.ActualWidth*dpi.DpiScaleX),h=(int)Math.Ceiling(root.ActualHeight*dpi.DpiScaleY);
   if(w>0&&h>0){var rt=new RenderTargetBitmap(w,h,dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);rt.Render(root);rt.Freeze();
    shot=new Image{Source=rt,Stretch=Stretch.Fill,Width=root.ActualWidth,Height=root.ActualHeight,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,IsHitTestVisible=false};
    Grid.SetRowSpan(shot,99);Grid.SetColumnSpan(shot,99);Panel.SetZIndex(shot,1000);root.Children.Add(shot);}}
  catch(Exception e){Log("Crossfade: "+e.Message);}
  change();
  if(shot!=null){var s=shot;Animate(s,UIElement.OpacityProperty,1,0,380,delegate{root.Children.Remove(s);});}
 }
 void HidePanel(){if(panel==null||!panel.IsVisible)return;panelTick.Stop();panel.Hide();UpdateTray();}

 // ------------------------------------------------------------ menu page
 void RefreshPanel(){
  if(panel==null)return;
  string source=currentSourcePath;bool empty=source==null;
  // empty state: no photo on the card, a word on why, and the way to the playlist
  P<TextBlock>("WallName").Text=!empty?System.IO.Path.GetFileNameWithoutExtension(source):Playlist.Count==0?"播放列表是空的":"没有可播放的图片";
  string folder=empty?null:System.IO.Path.GetDirectoryName(source);
  P<TextBlock>("WallFolder").Text=folder==null?"":System.IO.Path.GetFileName(folder);P<TextBlock>("WallFolder").ToolTip=folder;
  ((FrameworkElement)P<TextBlock>("WallFolder").Parent).Visibility=empty?Visibility.Collapsed:Visibility.Visible;
  P<Button>("EmptyAddBtn").Visibility=empty?Visibility.Visible:Visibility.Collapsed;
  foreach(string b in new[]{"PrevBtn","PlayBtn","NextBtn","OrderBtn","TimerBtn"})P<Button>(b).IsEnabled=!empty;
  if(!empty)SetCover(DisplayPath(source));else{coverPath=null;P<Image>("Cover").Source=null;P<Image>("CoverB").Source=null;}
  RefreshCoverStack();
  RefreshSettings();P<Border>("CardHit").ToolTip="播放列表 · "+Playlist.Count+" 项";P<TextBlock>("TimerText").Text=IntervalShort(settings.Interval);
  // Spatial on, but nothing on the playlist has a Spatial file yet: the photos play flat, and this says why
  RefreshSpatialNote();
  P<Button>("PrevBtn").IsEnabled=!empty&&slideBack.Count>0;
  SetSwitch(spatialEnabled);   // the row stays enabled: its page is where 3D files get made
  RefreshCube();
  RefreshSpatialBadge(source);
  bool seq=Sequential;Icon("OrderIcon",seq?ISequential:IShuffle);P<Button>("OrderBtn").ToolTip=seq?"按顺序播放 · 点一下改为随机":"随机播放 · 点一下改为按顺序";
  // stay / play
  var play=P<Button>("PlayBtn");play.Style=(Style)panel.FindResource(holdSlide?"Primary":"Pill");
  Icon("PlayIcon",holdSlide?IPlay:IPause,holdSlide?(Brush)ToneBrush:null);play.ToolTip=holdSlide?"播放":"暂停";
  UpdateCountdown();
 }
 void UpdateCountdown(){
  if(panel==null)return;
  if(currentSourcePath==null){P<TextBlock>("Countdown").Text=Playlist.Count==0?"添加文件夹或图片后开始播放":"列表里的位置找不到或没有图片";return;}
  var left=nextSlide-DateTime.Now;
  // what happens next (the length itself is on the timer button in the play row)
  string next=left.TotalHours>=1?(int)left.TotalHours+":"+left.Minutes.ToString("00")+":"+left.Seconds.ToString("00"):(int)left.TotalMinutes+":"+left.Seconds.ToString("00");
  // just the time (the length is on the timer button); the 3D note follows it after a dot
  P<TextBlock>("Countdown").Text=holdSlide?"已暂停":left.TotalSeconds<=0||(DateTime.Now-lastAdvance).TotalMilliseconds<1700?"正在换图…":left.TotalDays>1?"":next;
  P<TextBlock>("Countdown").ToolTip=holdSlide?"暂停中，不自动换图":"到下一张的时间";
  P<TextBlock>("SpatialBadgeDot").Visibility=P<TextBlock>("Countdown").Text.Length>0?Visibility.Visible:Visibility.Collapsed;
 }
 // Cover and swatch change by cross-fading, never by blinking empty.
 string coverPath;
 void SetCover(string path){
  if(string.Equals(path,coverPath,StringComparison.OrdinalIgnoreCase))return;coverPath=path;
  Task.Run(()=>Cover(path)).ContinueWith(t=>panel.Dispatcher.BeginInvoke(new Action(()=>{
   if(t.Result==null||coverPath!=path)return;var a=P<Image>("Cover");var b=P<Image>("CoverB");
   if(a.Source==null){a.Source=t.Result;return;}
   b.Source=t.Result;Animate(b,UIElement.OpacityProperty,0,1,500,delegate{if(b.Source==t.Result){a.Source=t.Result;b.BeginAnimation(UIElement.OpacityProperty,null);b.Opacity=0;}});})));
 }
 // The card (cover, name, folder, 3D · time) opens the playlist: on hover it lights up as a panel and a chevron shows
 // at its right. Buttons inside it (添加图片 when empty) keep their own clicks; the interval list (a popup, not inside
 // the card on screen) never reaches here.
 void HookCard(){
  var card=P<Border>("CardHit");var chev=P<ContentControl>("CardChevron");chev.Content=IconVisual(IChevron);
  Func<RoutedEventArgs,bool> own=e=>{for(var d=e.OriginalSource as DependencyObject;d!=null;d=d is Visual||d is System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(d):LogicalTreeHelper.GetParent(d)){if(d is Button)return false;if(d==card)return true;}return false;};
  card.MouseEnter+=delegate{card.Background=(Brush)panel.Resources["Fill"];Animate(chev,UIElement.OpacityProperty,chev.Opacity,1,160);FanCover(true);};
  card.MouseLeave+=delegate{card.Background=Brushes.Transparent;Animate(chev,UIElement.OpacityProperty,chev.Opacity,0,160);FanCover(false);};
  bool pressed=false;
  card.PreviewMouseLeftButtonDown+=(s,e)=>{pressed=own(e);if(pressed)card.Background=(Brush)panel.Resources["FillPress"];};
  card.PreviewMouseLeftButtonUp+=(s,e)=>{if(!pressed)return;pressed=false;card.Background=(Brush)panel.Resources["Fill"];
   if(own(e)){card.Background=Brushes.Transparent;FanCover(false);chev.BeginAnimation(UIElement.OpacityProperty,null);chev.Opacity=0;RefreshPlaylist(null);GoPage("playlist",1);}};
 }
 // ---- the cover's stack: the next photos (up to two, as many as the rotation has besides this one) behind the cover;
 // on hover the cover settles a little and they rise above it, each smaller and darker, as the playlist draws a folder.
 string[] coverStackPaths=new string[0];int coverStackShown;
 List<string> Upcoming(int n){
  var r=new List<string>();string cur=currentSourcePath;
  for(int i=slideAhead.Count-1;i>=0&&r.Count<n;i--){var p=slideAhead[i];if(!string.Equals(p,cur,StringComparison.OrdinalIgnoreCase)&&!r.Contains(p))r.Add(p);}
  for(int k=0;k<slideFiles.Count&&r.Count<n;k++){var p=slideFiles[(slideIndex+k)%slideFiles.Count];if(!string.Equals(p,cur,StringComparison.OrdinalIgnoreCase)&&!r.Contains(p,StringComparer.OrdinalIgnoreCase))r.Add(p);}
  return r;
 }
 void RefreshCoverStack(){
  var next=currentSourcePath==null?new List<string>():Upcoming(2);
  if(coverStackPaths!=null&&next.SequenceEqual(coverStackPaths,StringComparer.OrdinalIgnoreCase))return;
  coverStackPaths=next.ToArray();var paths=coverStackPaths;
  var backs=new[]{P<Border>("CoverBack1"),P<Border>("CoverBack2")};
  for(int i=0;i<2;i++){var b=backs[i];if(i>=paths.Length){b.Background=null;continue;}string p=paths[i];
   Task.Run(()=>{try{return Cover(DisplayPath(p));}catch{return null;}}).ContinueWith(t=>panel.Dispatcher.BeginInvoke(new Action(()=>{
    if(coverStackPaths!=paths||t.Result==null)return;b.Background=new ImageBrush(t.Result){Stretch=Stretch.UniformToFill};})));}
  if(card()!=null&&card().IsMouseOver)FanCover(true);
 }
 Border card(){return panel==null?null:P<Border>("CardHit");}
 void FanCover(bool open){
  int n=open&&coverStackPaths!=null?Math.Min(2,coverStackPaths.Length):0;if(!open&&coverStackShown==0)return;coverStackShown=n;
  var front=(ScaleTransform)P<Border>("CoverFront").RenderTransform;double fs=n>0?(n==2?.803:.9):1;
  var line=P<Border>("CoverLine");Animate(line,UIElement.OpacityProperty,line.Opacity,n>0?1:0,200);
  Animate(front,ScaleTransform.ScaleXProperty,front.ScaleX,fs,220);Animate(front,ScaleTransform.ScaleYProperty,front.ScaleY,fs,220);
  // bottom-anchored, so a card of scale s has its top at 66(1-s); each shows 6.5 px above the one in front, and the
  // whole stack is exactly the cover's height: with two behind, the cover settles to .803 and the last one's top is 0
  double[] scale=n==2?new[]{.723,.642}:new[]{.81,.72},rise=n==2?new[]{-11.78,-23.63}:new[]{-12.5,0};
  var backs=new[]{P<Border>("CoverBack1"),P<Border>("CoverBack2")};
  for(int i=0;i<2;i++){var b=backs[i];var g=(TransformGroup)b.RenderTransform;var s=(ScaleTransform)g.Children[0];var tr=(TranslateTransform)g.Children[1];bool on=i<n;
   double ts=on?scale[i]:.9,ty=on?rise[i]:0;
   Animate(s,ScaleTransform.ScaleXProperty,s.ScaleX,ts,240);Animate(s,ScaleTransform.ScaleYProperty,s.ScaleY,ts,240);Animate(tr,TranslateTransform.YProperty,tr.Y,ty,240);
   Animate(b,UIElement.OpacityProperty,b.Opacity,on?1:0,on?200:160);}
 }
 // Monochrome switch, always a white knob: on = brighter translucent track, off = faint track; colour and knob animate.
 bool? switchOn;
 // The photo's deep tone (Backdrop.Ambience): text and icons on white buttons, and the switch knob when on.
 Color toneColor=Color.FromRgb(0x2A,0x25,0x22);SolidColorBrush toneBrush;
 SolidColorBrush ToneBrush{get{if(toneBrush==null){toneBrush=new SolidColorBrush(toneColor);panel.Resources["Tone"]=toneBrush;}return toneBrush;}}
 // Brushes in use by a style get sealed by WPF and can no longer be animated, so every change animates a fresh brush
 // (from the colour shown now to the new one) and swaps it in.
 static SolidColorBrush Fading(Color from,Color to,int ms){var b=new SolidColorBrush(from);if(ms>0)b.BeginAnimation(SolidColorBrush.ColorProperty,new ColorAnimation(from,to,TimeSpan.FromMilliseconds(Math.Min(ms,800))));else b.Color=to;return b;}
 void SetTone(Color c,int ms){
  var from=ToneBrush.Color;toneColor=c;toneBrush=Fading(from,c,ms);panel.Resources["Tone"]=toneBrush;
  if(holdSlide)Icon("PlayIcon",IPlay,toneBrush);
  var knob=P<Ellipse>("SwitchKnob");if(switchOn==true){var k=knob.Fill as SolidColorBrush;knob.Fill=Fading(k==null?c:k.Color,c,ms);}
 }
 void SetSwitch(bool on){
  var track=P<Border>("SwitchTrack");var knob=P<Ellipse>("SwitchKnob");var move=(TranslateTransform)knob.RenderTransform;
  // on: the primary colour as track (white in the ambient style), knob in the tone; off: a faint track, white knob
  Color tOn=((SolidColorBrush)panel.Resources["PrimaryBg"]).Color,tOff=((SolidColorBrush)panel.Resources[styledLight==true?"Edge":"FillHover"]).Color,kOn=toneColor,kOff=Colors.White;
  if(switchOn==null){track.Background=new SolidColorBrush(on?tOn:tOff);knob.Fill=new SolidColorBrush(on?kOn:kOff);move.X=on?14:0;switchOn=on;return;}
  if(switchOn==on)return;switchOn=on;
  track.Background=Fading(((SolidColorBrush)track.Background).Color,on?tOn:tOff,220);
  knob.Fill=Fading(((SolidColorBrush)knob.Fill).Color,on?kOn:kOff,220);
  Animate(move,TranslateTransform.XProperty,on?0:14,on?14:0,260);
 }
 static BitmapSource Cover(string path){try{var b=new BitmapImage();b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.DecodePixelWidth=200;b.UriSource=new Uri(path);b.EndInit();b.Freeze();return UprightWpf(b,path);}catch{return null;}}
 // The wallpaper is changing (slideshow, 下一张, Spatial, delete): the open panel follows at once.
 DateTime lastAdvance;
 // newPhoto: another photo (the card says 正在换图 for a moment); the same one shown again (Spatial on / off, a new
 // background) is not a change of photo.
 void PanelWallpaperChanged(string display,uint color,int ms,bool newPhoto=true){if(newPhoto)lastAdvance=DateTime.Now;if(!PanelOpen)return;SetAmbient(display,ms);RefreshPanel();}

 // ------------------------------------------------------------ colour helpers
 bool dropping;   // a dialog or popup of the panel's own is open: losing focus to it does not hide the panel
 // ------------------------------------------------------------ motion + helpers
 // Pages slide 28 px and cross-fade; the panel itself keeps its size (both pages are measured, see GlassPanel.xaml).
 // The page that leaves stops taking clicks at once (it sits on top while it fades) and is hidden when its fade ends.
 void SlidePages(FrameworkElement from,FrameworkElement to,int dir){
  var tf=(TranslateTransform)from.RenderTransform;var tt=(TranslateTransform)to.RenderTransform;
  to.Visibility=Visibility.Visible;to.IsHitTestVisible=true;from.IsHitTestVisible=false;
  Animate(tt,TranslateTransform.XProperty,28*dir,0,320,null);Animate(to,UIElement.OpacityProperty,0,1,240,null);
  Animate(tf,TranslateTransform.XProperty,0,-28*dir,320,null);
  Animate(from,UIElement.OpacityProperty,1,0,180,delegate{if(!from.IsHitTestVisible)Settle(from,false);});
 }
 // Puts a page in its resting state: animations removed (they would otherwise keep holding their last values).
 static void Settle(FrameworkElement page,bool visible){
  page.BeginAnimation(UIElement.OpacityProperty,null);var t=(TranslateTransform)page.RenderTransform;t.BeginAnimation(TranslateTransform.XProperty,null);
  page.Opacity=1;t.X=0;page.IsHitTestVisible=visible;if(page.Visibility!=Visibility.Collapsed)page.Visibility=visible?Visibility.Visible:Visibility.Hidden;
 }
 // done runs when the animation ends (it has to be attached before the animation starts to fire at all).
 static void Animate(Animatable target,DependencyProperty p,double from,double to,int ms,EventHandler done=null){
  var a=new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(ms)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};
  Timeline.SetDesiredFrameRate(a,UiFrameRate);
  if(done!=null)a.Completed+=done;target.BeginAnimation(p,a);
 }
 static void Animate(UIElement target,DependencyProperty p,double from,double to,int ms,EventHandler done=null){
  var a=new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(ms)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};
  Timeline.SetDesiredFrameRate(a,UiFrameRate);
  if(done!=null)a.Completed+=done;target.BeginAnimation(p,a);
 }
 void Drag(FrameworkElement el,Action<Point> f){
  el.MouseLeftButtonDown+=(s,e)=>{el.CaptureMouse();f(e.GetPosition(el));e.Handled=true;};
  el.MouseMove+=(s,e)=>{if(el.IsMouseCaptured)f(e.GetPosition(el));};
  el.MouseLeftButtonUp+=(s,e)=>{el.ReleaseMouseCapture();};
 }
 static double Clamp(double v){return Math.Max(0,Math.Min(1,v));}
 static string Hex(Color c){return "#"+c.R.ToString("X2")+c.G.ToString("X2")+c.B.ToString("X2");}
 static Color Hsv(double h,double s,double v){double c=v*s,x=c*(1-Math.Abs((h/60)%2-1)),m=v-c,r=0,g=0,b=0;
  if(h<60){r=c;g=x;}else if(h<120){r=x;g=c;}else if(h<180){g=c;b=x;}else if(h<240){g=x;b=c;}else if(h<300){r=x;b=c;}else{r=c;b=x;}
  return Color.FromRgb((byte)Math.Round((r+m)*255),(byte)Math.Round((g+m)*255),(byte)Math.Round((b+m)*255));}
 static void ToHsv(Color c,out double h,out double s,out double v){double r=c.R/255.0,g=c.G/255.0,b=c.B/255.0,max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),d=max-min;v=max;s=max<=0?0:d/max;
  if(d<=0)h=0;else if(max==r)h=60*(((g-b)/d)%6);else if(max==g)h=60*((b-r)/d+2);else h=60*((r-g)/d+4);if(h<0)h+=360;}

 // Line icons on a 24-unit grid (stroke 2.4 there = 1.6 px at 16 px).
 const string IPause="M8.5 5.5v13M15.5 5.5v13",IPlay="M7.5 5.2l11 6.8-11 6.8z",INext="M5.5 5.5l9 6.5-9 6.5z M18.5 5.5v13",
  
  ICube="M12 3l8 4.5v9L12 21l-8-4.5v-9z M4 7.5l8 4.5 8-4.5M12 12v9",IOpen="M13.5 4.5h6v6M19.5 4.5L11 13M17 14v4.5a1.5 1.5 0 0 1-1.5 1.5h-10A1.5 1.5 0 0 1 4 18.5v-10A1.5 1.5 0 0 1 5.5 7H10",
  ITrash="M4.5 7h15M9.5 7V5h5v2M6.5 7l.9 12.2a1.5 1.5 0 0 0 1.5 1.3h6.2a1.5 1.5 0 0 0 1.5-1.3L17.5 7",IExit="M10 4.5H6a1.5 1.5 0 0 0-1.5 1.5v12A1.5 1.5 0 0 0 6 19.5h4M14 8l4 4-4 4M18 12H9",
  ILock="M7.6 10.5h8.8a2.6 2.6 0 0 1 2.6 2.6v4.8a2.6 2.6 0 0 1-2.6 2.6H7.6A2.6 2.6 0 0 1 5 17.9v-4.8a2.6 2.6 0 0 1 2.6-2.6z M8 10.5V8a4 4 0 0 1 8 0v2.5",IBack="M14.5 6l-6 6 6 6",
  IPrev="M18.5 5.5l-9 6.5 9 6.5z M5.5 5.5v13",
  
  IList="M9 6.5h11M9 12h11M9 17.5h11 M4.6 6.5h.1M4.6 12h.1M4.6 17.5h.1",IChevron="M9.5 6l6 6-6 6",IClose="M7 7l10 10M17 7L7 17",
  IShuffle="M4 7h3.2c1.8 0 2.9.9 4.1 2.9l1.4 2.2c1.2 2 2.3 2.9 4.1 2.9H20 M4 17h3.2c1.2 0 2.1-.4 2.9-1.3 M13.9 8.3c.8-.9 1.7-1.3 2.9-1.3H20 M17.5 4.5L20 7l-2.5 2.5 M17.5 14.5L20 17l-2.5 2.5",
  ISequential="M4 7h12 M4 12h12 M4 17h7 M15 14.5l4 2.5-4 2.5",
  IBattery="M5 7.5h11.5A1.5 1.5 0 0 1 18 9v6a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 15V9A1.5 1.5 0 0 1 5 7.5z M21 10.5v3 M7 10.5v3",
  IPower="M12 3.5v7.5 M7.2 6.3a7.5 7.5 0 1 0 9.6 0",
  // 壁纸背景: a picture on a larger ground
  IBackdrop="M5 4.5h14A1.5 1.5 0 0 1 20.5 6v12a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 18V6A1.5 1.5 0 0 1 5 4.5z M9.5 8.5h5a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1h-5a1 1 0 0 1-1-1v-5a1 1 0 0 1 1-1z",
  // Spatial camera: the pointer; two opposite arrows; an orbit around a point
  IFollow="M6 4.5l12.5 6-5.4 1.7-2.3 5.3z",
  IDrift="M12 7.5c4.7 0 8.5 2 8.5 4.5s-3.8 4.5-8.5 4.5S3.5 14.5 3.5 12 7.3 7.5 12 7.5z M12 11.9v.2 M18.2 8.6h.1",
  IContrast="M12 3.5a8.5 8.5 0 1 0 0 17a8.5 8.5 0 1 0 0-17z M12 3.5v17 M12 7.5h4.5 M12 11.5h6.4 M12 15.5h5",
  
  IGear="M12 15a3 3 0 1 0 0-6a3 3 0 1 0 0 6z M19.4 13.5a7.5 7.5 0 0 0 0-3l2-1.6-2-3.4-2.4.9a7.5 7.5 0 0 0-2.6-1.5L14 2.5h-4l-.4 2.4a7.5 7.5 0 0 0-2.6 1.5l-2.4-.9-2 3.4 2 1.6a7.5 7.5 0 0 0 0 3l-2 1.6 2 3.4 2.4-.9a7.5 7.5 0 0 0 2.6 1.5l.4 2.4h4l.4-2.4a7.5 7.5 0 0 0 2.6-1.5l2.4.9 2-3.4z",ICheck="M5.5 12.5l4 4 9-9",
  // 安装 SHARP: downloading, something short (disk, memory), the graphics card
  IDownload="M12 4.5v10 M7.5 10.5l4.5 4.5 4.5-4.5 M5 19.5h14",IAlert="M12 3.5a8.5 8.5 0 1 0 0 17a8.5 8.5 0 1 0 0-17z M12 8v4.5 M12 16v.1",
  IChip="M8.5 7h7A1.5 1.5 0 0 1 17 8.5v7a1.5 1.5 0 0 1-1.5 1.5h-7A1.5 1.5 0 0 1 7 15.5v-7A1.5 1.5 0 0 1 8.5 7z M10 3.5V7 M14 3.5V7 M10 17v3.5 M14 17v3.5 M3.5 10H7 M3.5 14H7 M17 10h3.5 M17 14h3.5",IDot="M12 12h.1",
  IFolder="M3.5 7A1.5 1.5 0 0 1 5 5.5h4.2l2 2.2H19A1.5 1.5 0 0 1 20.5 9.2v8.3A1.5 1.5 0 0 1 19 19H5a1.5 1.5 0 0 1-1.5-1.5z",
  IImage="M5.5 4.5h13A1.5 1.5 0 0 1 20 6v12a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 18V6a1.5 1.5 0 0 1 1.5-1.5z M4.5 16l4.5-4.5 4 4 2.5-2.5 4 4 M15.5 9a.5.5 0 1 0 0-.1z";
 void Icon(string host,string data,Brush brush=null){
  var p=new Path{Data=Geometry.Parse(data),StrokeThickness=2.4,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};
  if(brush!=null)p.Stroke=brush;else p.SetBinding(Shape.StrokeProperty,new System.Windows.Data.Binding("Foreground"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor,typeof(Control),1)});
  if(data==IPlay){if(brush!=null)p.Fill=brush;else p.SetBinding(Shape.FillProperty,new System.Windows.Data.Binding("Foreground"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor,typeof(Control),1)});}
  var canvas=new Canvas{Width=24,Height=24};canvas.Children.Add(p);var vb=new Viewbox{Child=canvas};EvenStroke(vb,p);P<ContentControl>(host).Content=vb;
 }
 // Every icon's line is the same width on screen (IconStroke px), whatever size it is drawn at: the 24-unit drawing
 // is scaled, so its stroke is set for the size it gets.
 const double IconStroke=1.55;
 static void EvenStroke(Viewbox vb,Path p){
  Action set=()=>{double w=vb.ActualWidth>0?vb.ActualWidth:(vb.Parent as FrameworkElement)!=null?((FrameworkElement)vb.Parent).Width:double.NaN;if(w>0)p.StrokeThickness=IconStroke*24/w;};
  vb.SizeChanged+=delegate{set();};set();
 }
}
