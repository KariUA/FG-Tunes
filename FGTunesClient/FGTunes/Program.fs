namespace AvaloniaFuncUIApp1

open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.DSL
open Avalonia.Input
open Avalonia.Themes.Fluent
open Avalonia.FuncUI.Hosts
open Avalonia.FuncUI

type MainWindow() as this =
    inherit HostWindow()
    do
        base.Title <- "FG TUNES"
        base.Width <- 800.0
        base.Height <- 900.0
        
        //La ventana se puede redimensionar
        base.CanResize <- true

        this.Content <- Player.view
        
type App() =
    inherit Application() 

    override this.Initialize() = 
        this.Styles.Add (FluentTheme(baseUri = null, Mode = FluentThemeMode.Dark))

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktopLifetime ->
            desktopLifetime.MainWindow <- MainWindow()
        | _ -> ()

module Program =

    [<EntryPoint>]
    let main(args: string[]) =
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .UseSkia()
            .StartWithClassicDesktopLifetime(args)