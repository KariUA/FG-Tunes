namespace Avalonia.FuncUI.DSL

open Avalonia
open Avalonia.Collections
open Avalonia.Controls
open Avalonia.Media

module Player =

    open Avalonia.FuncUI
    open Avalonia.FuncUI.DSL
    open Avalonia.Layout
    open System.IO
    open System.Net.Sockets
    open System.Threading.Tasks
    open System
    open System.Threading
    open NAudio.Wave

    let mutable minSongDuration = ""
    let mutable maxSongDuration = ""
    
    let mutable songList = []
    let mutable top5Songs = []
    
    let mutable filterSong = []
    
    let mutable songDuration = []
    
    let serverHost = "localhost"
    let serverPort = 8000
    let mutable currentSongName = ""

    // Función  global para reproducir una canción en un hilo separado (no es óptimo,pero funciona :D)
    let mutable client = new TcpClient(serverHost, serverPort)
    let mutable serverStream = client.GetStream()
    let mutable writer = new StreamWriter(serverStream)
    let mutable reader = new StreamReader(serverStream)
    
    let trim (str: string) =
        str.Trim()

    let mutable isPaused = false 
    let mutable isStopped = false
    //let mutable currentSongName = ""
    let mutable currentPlaybackThread : Thread option = None
    let mutable currentPositionMs = 0L
    let mutable advanceSong = false
    let mutable rewindSong = false

    type Playlist =
        { Name: string
          Songs: string list }
        

    // Función para adelantar la canción
    let advanceSongBy5Seconds () =
        advanceSong <- true

    // Función para retroceder la canción
    let rewindSongBy5Seconds () =
        rewindSong <- true

    // Función para reproducir una canción en un hilo separado
    let playSongInBackground (songBytes: byte[], resumeFrom: int64 option) =  //Reproducir canción en segundo plano
        currentPlaybackThread <- Some(Thread.CurrentThread)
        Task.Run(fun () ->
            
            let isValidMp3Data = songBytes.Length > 0

            if isValidMp3Data then
                
                use songData = new MemoryStream(songBytes) 
                songData.Position <- 0L 

                if resumeFrom.IsSome then
                    // Si estamos reanudando, configuramos la posición de reproducción
                    songData.Position <- resumeFrom.Value
                    currentPositionMs <- resumeFrom.Value
                
                use mp3Stream = new Mp3FileReader(songData) 
                use waveOut = new WaveOutEvent() 
                
                try
                    waveOut.Init(mp3Stream)
                    waveOut.Play()
                    Console.WriteLine("Reproduciendo...")
                    
                    // Esperar a que se establezca la señal de detener la reproducción y verificar el estado de pausa
                    while waveOut.PlaybackState = PlaybackState.Playing && not isStopped && not isPaused  do
                        // Verificar si se ha recibido un comando para adelantar o retroceder la canción
                        if advanceSong then
                            currentPositionMs <- currentPositionMs + 5000L // Adelantar 5 segundos
                            mp3Stream.CurrentTime <- TimeSpan.FromMilliseconds(float currentPositionMs)
                            advanceSong <- false // Reiniciar la bandera
                        elif rewindSong then
                            currentPositionMs <- max 0L (currentPositionMs - 5000L) // Retroceder 5 segundos
                            mp3Stream.CurrentTime <- TimeSpan.FromMilliseconds(float currentPositionMs)
                            rewindSong <- false // Reiniciar la bandera
                        System.Threading.Thread.Sleep(10)

                    // Si se recibe la señal de detener, detener la reproducción
                    if isStopped then
                        Console.WriteLine("Deteniendo...")
                        waveOut.Stop()
                        
                    // Si se recibe la señal de pausa, detener la reproducción
                    if isPaused then
                        waveOut.Pause()
                        Console.WriteLine("Pausado...")
                        
                        
                with
                    | :? Exception as ex ->
                        Console.WriteLine($"Error al reproducir la canción: {ex.Message}")
            else
                Console.WriteLine("Los datos no parecen ser un archivo MP3 válido.")
        )

    let asyncReadFromServer () = //Leer respuesta del servidor
        async {
            while true do
                let! response = Async.AwaitTask (reader.ReadLineAsync())
                if response = null then
                    return ()
                else
                    Console.WriteLine(response)
        }

    let getListOfSongs () = //Obtener lista de canciones
        async {
            try
                let command = "1"
                writer.WriteLine(command)
                writer.Flush()

                use serverReader = new StreamReader(serverStream)
                let response = serverReader.ReadLine()

                if not (String.IsNullOrWhiteSpace(response)) then
                    let songs = response.Split(',') // Separar las canciones por el caracter
                    songList <- List.ofArray songs
                else
                    Console.WriteLine("No se encontraron canciones en el servidor.")
            with
                | :? Exception as ex ->
                    Console.WriteLine($"Error al listar canciones: {ex.Message}")
        }
    
    //--------------------------------------------- METODOS DE REPRODUCCION ---------------------------------------------
    let asyncPlaySong ( songName : string, numCommand : string, resumeFrom: int64 option) = //Reproducir canción
        async {
            try
                isPaused <- false
                isStopped <- false
                
                //Resstablece conexión con el servidor
                client.Close()
                serverStream.Close()
                writer.Close()
                reader.Close()
                
                client <- new TcpClient(serverHost, serverPort)
                serverStream <- client.GetStream()
                writer <- new StreamWriter(serverStream)
                reader <- new StreamReader(serverStream)
                Console.WriteLine("Conectado con el servidor.")
                
                
                let command = sprintf "%s %s" numCommand songName 
                writer.WriteLine(command) 
                writer.Flush()

                // Leer la longitud de los datos de la canción
                let bufferSize = 100_000_000
                let lengthBuffer = Array.zeroCreate bufferSize
                let bytesRead = serverStream.Read(lengthBuffer, 0, bufferSize)

                let songLength = BitConverter.ToInt32(lengthBuffer, 0)

                // Leer los datos de la canción según la longitud
                let songBytes = Array.zeroCreate songLength
                let mutable bytesReadTotal = 0
                let mutable allDataReceived = false

                let rec receiveData (songBytes: byte[]) =
                    if bytesReadTotal < songLength then
                        // Leer datos adicionales si aún no hemos recibido todo
                        let remainingBytes = songLength - bytesReadTotal
                        let bytesToRead = min remainingBytes songBytes.Length
                        let bytesReadNow = serverStream.Read(songBytes, bytesReadTotal, bytesToRead)
                        if bytesReadNow = 0 then
                            serverStream.Close()
                        else 
                            bytesReadTotal <- bytesReadTotal + bytesReadNow
                            receiveData songBytes
                    else
                        // Todos los datos se han recibido
                        if not allDataReceived then
                            allDataReceived <- true
                        ()

                receiveData songBytes

                // Iniciar la reproducción de la canción en segundo plano
                playSongInBackground (songBytes, resumeFrom) |> ignore
                        
            with
                | :? Exception as ex ->
                    Console.WriteLine($"Error al reproducir la canción: {ex.Message}")
    }
    
    let asyncStopSong () =
        async {
            try
                isStopped <- true
                isPaused <- false
                let command = "3" 
                writer.WriteLine(command)
                writer.Flush()      

                // Cerrar la conexión después de detener la reproducción
                serverStream.Close()
            with
                | :? Exception as ex ->
                    Console.WriteLine($"Error al detener la reproducción: {ex.Message}")
        }

    let asyncPauseSong () = //Pausar canción
        async {
            try
                isPaused <- true
                isStopped <- false
                let command = "4" 
                writer.WriteLine(command)
                writer.Flush()      

                // Cerrar la conexión después de pausar la reproducción
                serverStream.Close()
            with
                | :? Exception as ex ->
                    Console.WriteLine($"Error al detener la reproducción: {ex.Message}")
        }

    let asyncResumeSong (currentSongName : string) = //Reanudar canción
        async {
            try
                isPaused <- false
                isStopped <- false
                match currentPlaybackThread with
                | Some(thread) ->
                    // Reanudar desde el punto de pausa
                    let resumePosition = currentPositionMs
                    asyncPlaySong (currentSongName, "5", Some(resumePosition)) |> Async.StartImmediate
                | None ->
                    Console.WriteLine("No se puede reanudar, no hay una canción en pausa.")
            with
                | :? Exception as ex ->
                    Console.WriteLine($"Error al detener la reproducción: {ex.Message}")
        }

    //--------------------------------------------- METODOS DE BUSQUEDA ---------------------------------------------
    

    let asyncSearchSong (filter : string) = //Buscar canción
        async {
            try
                
                Console.WriteLine("ENTRA AL BUSCAR CON: " + filter)
                let command = sprintf "6 %s" filter // Comando para buscar canciones por filtro
                writer.WriteLine(command)
                writer.Flush()

                use serverReader = new StreamReader(serverStream)
                let response = serverReader.ReadLine()
                
                Console.WriteLine("Response: " + response)

                if not (String.IsNullOrWhiteSpace(response)) then
                    let songs = response.Split(',') // Separar las canciones por el caracter
                    filterSong <- List.ofArray songs
                    
                else
                    Console.WriteLine("No se encontraron canciones con el filtro " + filter + ".")
            with
                | :? Exception as ex ->
                Console.WriteLine($"Error al listar canciones: {ex.Message}")
                
        }
      
    let asyncSearchTop5 () = //Buscar top 5 canciones
        async {
            try
                let command = "7" // Comando para buscar las 5 canciones más escuchadas
                writer.WriteLine(command)
                writer.Flush()

                use serverReader = new StreamReader(serverStream)
                let response = serverReader.ReadLine()

                if not (String.IsNullOrWhiteSpace(response)) then
                    let songs = response.Split(',') // Separar las canciones por el caracter
                    top5Songs <- List.ofArray songs
                    
                else
                    Console.WriteLine("No se encontraron canciones en el servidor.")
            with
                | :? Exception as ex ->
                    Console.WriteLine($"Error al listar canciones Top 5: {ex.Message}")
        }
        
    let asyncSearchByDuration (min : string, max : string) = //Buscar canciones por duración
        async {
            try
                Console.WriteLine("ENTRA AL BUSCAR CON: " + min + " y " + max)
                let command = sprintf "8 %s %s" min max // Comando para buscar canciones por duración
                writer.WriteLine(command)
                writer.Flush()

                use serverReader = new StreamReader(serverStream)
                let response = serverReader.ReadLine()
               

                if not (String.IsNullOrWhiteSpace(response)) then
                    let songs = response.Split(',') // Separar las canciones por el caracter
                    songDuration <- List.ofArray songs
                    
                else
                    Console.WriteLine("No se encontraron canciones en el servidor.")
            with
                | :? Exception as ex ->
                    Console.WriteLine($"Error al listar canciones: {ex.Message}")
        }

    let connectServer () =
        async {
            try
                // Conectar con el servidor
                client <- new TcpClient(serverHost, serverPort)
                serverStream <- client.GetStream()
                writer <- new StreamWriter(serverStream)
                reader <- new StreamReader(serverStream)
                Console.WriteLine("Conectado con el servidor.")
            with
                | :? Exception as ex ->
                    Console.WriteLine($"Error al conectar con el servidor: {ex.Message}")
        
        }
        
 
    //--------------------------------------------- INTERFAZ GRAFICA ---------------------------------------------
    
    let view = //Problema a resolver: No se puede reproducir una cancion por la conexion con el servidor
        
        connectServer () |> Async.StartImmediate
        getListOfSongs () |> Async.StartImmediate
        

        Component (fun _ ->
            let mutable selectedSong = "" // Canción seleccionada en la lista
            let songs = songList |> List.map trim // Lista de canciones sin espacios en blanco
            let mutable filter = "" // Filtro de búsqueda
            let mutable minSongDuration = "" // Duración mínima de la canción
            let mutable maxSongDuration = "" // Duración máxima de la canción
            let mutable playlist = { Name = ""; Songs = [] } // Playlist seleccionada
            let mutable lisPlaylists = [] // Lista de playlists
            let mutable playlistName = "" // Nombre de la playlist
            let mutable filterSong = AvaloniaList<string>() // Lista de canciones filtradas
            let mutable songDuration = [] // Lista de canciones filtradas por duración
            let mutable top5Songs = [] // Lista de las 5 canciones más escuchadas
            
           
            DockPanel.create [ //Panel principal de la ventana
                
                DockPanel.verticalAlignment VerticalAlignment.Stretch //Alineación vertical del panel principal de la ventana en la parte inferior 
                DockPanel.horizontalAlignment HorizontalAlignment.Stretch
                
                DockPanel.children [

                    //Bienvenida al usuario
                    TextBlock.create [
                        TextBlock.dock Dock.Top
                        TextBlock.fontSize 25.0
                        TextBlock.text "Welcome to FG TUNES"
                        TextBlock.fontStyle FontStyle.Italic
                        TextBlock.horizontalAlignment HorizontalAlignment.Center
                        TextBlock.margin (Thickness(0.0, 10.0, 0.0, 10.0))
                    ]
                    
                    // Titulo de la lista de canciones
                    TextBlock.create [
                        TextBlock.dock Dock.Top
                        TextBlock.fontSize 20.0
                        TextBlock.text "List of Songs"
                        TextBlock.horizontalAlignment HorizontalAlignment.Center
                        TextBlock.margin (Thickness(0.0, 10.0, 0.0, 10.0))
                    ]
                     
                    // Lista de canciones
                    ListBox.create [
                        ListBox.dock Dock.Top
                        ListBox.width 300
                        ListBox.height 200
                        ListBox.dataItems songs // Asignar la lista de canciones
                        ListBox.onSelectionChanged (fun args ->
                            let selectedItems = args.AddedItems // Obtener los elementos seleccionados
                            if selectedItems.Count > 0 then 
                                let selectedItem = selectedItems.Item(0) // Obtener el primer elemento seleccionado
                                selectedSong <- selectedItem.ToString()
                                Console.WriteLine("Selected song: " + selectedSong)
                            else
                                // No se seleccionó ninguna canción
                                selectedSong <- ""
                                Console.WriteLine("No se seleccionó ninguna canción.")
                        )
                        ListBox.margin (Thickness(0.0, 10.0, 0.0, 10.0))
                    ]
                    
//-----------------------------------------------------------  Controles de reproducción -----------------------------------------------------------
                    StackPanel.create [
                        StackPanel.dock Dock.Top
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.horizontalAlignment HorizontalAlignment.Center
                        StackPanel.children [

                            Button.create [  // Botón para reproducir la canción seleccionada
                                Button.width 90
                                Button.content "Play"
                                Button.onClick (fun _ ->
                                    if selectedSong <> "" then
                                        // Obtener la lista de canciones
                                        Console.WriteLine("Se seleccionó la canción: " + selectedSong + " para reproducir.")
                                        asyncPlaySong (selectedSong, "2", None) |> Async.StartAsTask |> ignore
                                    else
                                        Console.WriteLine("No se seleccionó ninguna canción.")
                                )
                                Button.margin (Thickness(5.0))
                            ]

                            Button.create [  // Botón para detener la canción seleccionada
                                Button.width 90
                                Button.content "Stop"
                                Button.onClick (fun _ ->
                                    if selectedSong <> "" then
                                        // Obtener la lista de canciones
                                        Console.WriteLine("Se seleccionó la canción: " + selectedSong + " para detener.")
                                        asyncStopSong () |> Async.StartAsTask |> ignore
                                    else
                                        Console.WriteLine("No se seleccionó ninguna canción.")
                                )
                                Button.margin (Thickness(5.0))
                            ]

                            Button.create [  // Botón para pausar la canción seleccionada
                                Button.width 90
                                Button.content "Pause"
                                Button.onClick (fun _ ->
                                    if selectedSong <> "" then
                                        // Obtener la lista de canciones
                                        Console.WriteLine("Se seleccionó la canción: " + selectedSong + " para pausar.")
                                        asyncPauseSong () |> Async.StartImmediate
                                    else
                                        Console.WriteLine("No se seleccionó ninguna canción.")
                                )
                                Button.margin (Thickness(5.0))
                            ]

                            Button.create [  // Botón para reanudar la canción seleccionada
                                Button.width 90
                                Button.content "Resume"
                                Button.onClick (fun _ ->
                                    if selectedSong <> "" then
                                        // Obtener la lista de canciones
                                        Console.WriteLine("Se seleccionó la canción: " + selectedSong + " para reanudar.")
                                        asyncResumeSong (selectedSong) |> Async.StartImmediate
                                    else
                                        Console.WriteLine("No se seleccionó ninguna canción.")
                                )
                                Button.margin (Thickness(5.0))
                            ]

                            Button.create [  // Botón para adelantar la canción seleccionada
                                Button.width 90
                                Button.content "5s >>>"
                                Button.onClick (fun _ ->
                                    if selectedSong <> "" then
                                        // Obtener la lista de canciones
                                        Console.WriteLine("Se seleccionó la canción: " + selectedSong + " para adelantar.")
                                        advanceSongBy5Seconds () 
                                    else
                                        Console.WriteLine("No se seleccionó ninguna canción.")
                                )
                                Button.margin (Thickness(5.0))
                            ]

                            Button.create [  // Botón para retroceder la canción seleccionada
                                Button.width 90
                                Button.content "<<< 5s"
                                Button.onClick (fun _ ->
                                    if selectedSong <> "" then
                                        // Obtener la lista de canciones
                                        Console.WriteLine("Se seleccionó la canción: " + selectedSong + " para retroceder.")
                                        rewindSongBy5Seconds () 
                                    else
                                        Console.WriteLine("No se seleccionó ninguna canción.")
                                )
                                Button.margin (Thickness(5.0))
                            ]
                        ]
                    ]
                    
//----------------------------------------------------------- Controles de búsqueda -----------------------------------------------------------
                    StackPanel.create [
                        StackPanel.dock Dock.Top
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.horizontalAlignment HorizontalAlignment.Center
                        StackPanel.children [

                            TextBox.create [
                                TextBox.width 260
                                TextBox.height 30
                                TextBox.text "Search by name, artist or album..."
                                TextBox.onTextChanged (fun args ->
                                    filter <- args.ToString()
                                    Console.WriteLine("Filter: " + filter)
                                )
                                TextBox.margin (Thickness(5.0))
                            ]

                            TextBox.create [
                                TextBox.width 100
                                TextBox.height 30
                                TextBox.text "Min"
                                TextBox.onTextChanged (fun args ->
                                    minSongDuration <- args.ToString()
                                    Console.WriteLine("Min: " + minSongDuration)
                                )
                                TextBox.margin (Thickness(5.0))
                            ]

                            TextBox.create [
                                TextBox.width 100
                                TextBox.height 30
                                TextBox.text "Max"
                                TextBox.onTextChanged (fun args ->
                                    maxSongDuration <- args.ToString()
                                    Console.WriteLine("Max: " + maxSongDuration)
                                )
                                TextBox.margin (Thickness(5.0))
                            ]
                            let myFilteredListBox=ListBox()
                            let filterSong = AvaloniaList<string>()// Inicializa con la lista original de canciones


                            Button.create [
                                Button.width 90
                                Button.content "Search"
                                Button.onClick (fun _ ->
                                    connectServer () |> Async.StartImmediate
                                    if filter <> "" && filter <> "Search by name, artist or album..." then
                                        asyncSearchSong(filter) |> Async.RunSynchronously |> ignore
                                        let filteredList = songList |> List.filter (fun song -> song.Contains(filter))
                                        filterSong.Clear() // Limpia la colección actual
                                        for item in filteredList do
                                            filterSong.Add(item) // Agrega los elementos filtrados a filterSong
                                    elif minSongDuration <> "" && maxSongDuration <> "" then
                                        asyncSearchByDuration(minSongDuration, maxSongDuration) |> Async.StartAsTask |> ignore
                                        songDuration <- songDuration |> List.map trim
                                        filterSong.Clear()
                                        for item in songDuration do
                                            filterSong.Add(item)
                                    else
                                        Console.WriteLine("No se agregó ningún filtro.")
                                )
                                Button.margin (Thickness(5.0))
                            ]
                        ]
                        Button.margin (Thickness(5.0))
                    ]
// ----------------------------------------------------------- Lista de resultados de búsqueda -----------------------------------------------------------
                    StackPanel.create [
                        StackPanel.dock Dock.Top
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.horizontalAlignment HorizontalAlignment.Center
                        StackPanel.children [

                            // Titulo de la lista de canciones filtradas
                            TextBlock.create [
                                TextBlock.dock Dock.Top
                                TextBlock.fontSize 20.0
                                TextBlock.text "Search Results"
                                TextBlock.horizontalAlignment HorizontalAlignment.Center
                                TextBlock.margin (Thickness(0.0, 10.0, 0.0, 10.0))
                            ]

                            // Lista de canciones filtradas
                            ListBox.create [
                                ListBox.width 300
                                ListBox.height 200
                                ListBox.dataItems filterSong // Asignar la lista de canciones
                                ListBox.onSelectionChanged (fun args ->
                                    let selectedItems = args.AddedItems
                                    if selectedItems.Count > 0 then
                                        let selectedItem = selectedItems.[0] :?> string // Cast the selected item to a string
                                        selectedSong <- selectedItem
                                        Console.WriteLine("Selected song: " + selectedSong)
                                    else
                                        selectedSong <- ""
                                        Console.WriteLine("No se seleccionó ninguna canción.")
                                )
                                ListBox.margin (Thickness(0.0, 10.0, 0.0, 10.0))
                            ]
                        ]
                        
                    ]
// ----------------------------------------------------------- Lista de canciones Top 5 -----------------------------------------------------------
                    
                    StackPanel.create [
                        StackPanel.dock Dock.Top
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.horizontalAlignment HorizontalAlignment.Center
                        StackPanel.children [

                            // Titulo de la lista de canciones Top 5
                            TextBlock.create [
                                TextBlock.dock Dock.Top
                                TextBlock.fontSize 20.0
                                TextBlock.text "Top 5 Songs"
                                TextBlock.horizontalAlignment HorizontalAlignment.Center
                                TextBlock.margin (Thickness(0.0, 10.0, 0.0, 10.0))
                            ]

                            // Lista de canciones Top 5
                            ListBox.create [
                                ListBox.width 300
                                ListBox.height 200
                                ListBox.dataItems top5Songs // Asignar la lista de canciones
                                ListBox.onSelectionChanged (fun args ->
                                    let selectedItems = args.AddedItems
                                    if selectedItems.Count > 0 then
                                        let selectedItem = selectedItems.[0] :?> string // Cast the selected item to a string
                                        selectedSong <- selectedItem
                                        Console.WriteLine("Selected song: " + selectedSong)
                                    else
                                        selectedSong <- ""
                                        Console.WriteLine("No se seleccionó ninguna canción Top 5.")
                                )
                                ListBox.margin (Thickness(0.0, 10.0, 0.0, 10.0))
                            ]
                        ]
                    ]
                    

                    // ----------------------------------------------------------- Lista de resultados de búsqueda -----------------------------------------------------------


                    // Titulo de la lista de playlists
                    TextBlock.create [
                        TextBlock.dock Dock.Top
                        TextBlock.fontSize 20.0
                        TextBlock.text "List of Playlists"
                        TextBlock.horizontalAlignment HorizontalAlignment.Center
                        TextBlock.margin (Thickness(0.0, 10.0, 0.0, 10.0))
                    ]

                    let mutable selectedPlaylistName = ""

                    //----------------------------------------------------------- Controles de playlists -----------------------------------------------------------
                    StackPanel.create [
                        StackPanel.dock Dock.Top
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.horizontalAlignment HorizontalAlignment.Center
                        StackPanel.children [

                            TextBox.create [
                                TextBox.width 200
                                TextBox.height 30
                                TextBox.text "Name playlist"
                                TextBox.onTextChanged (fun args ->
                                    playlistName <- args.ToString()
                                    Console.WriteLine("Playlist name: " + playlistName)
                                )
                                TextBox.margin (Thickness(5.0))
                            ]

                            Button.create [  // Botón para crear playlist
                                Button.width 120
                                Button.content "Add Playlist"
                                Button.onClick (fun _ ->
                                    if playlistName <> "" && playlistName <> "Name playlist" then
                                        Console.WriteLine("Se seleccionó la playlist: " + playlistName + " para crear.")
                                        let newPlaylist = { Name = playlistName; Songs = [] }
                                        lisPlaylists <- lisPlaylists @ [newPlaylist]
                                        Console.WriteLine("Playlists: " + lisPlaylists.ToString())
                                        playlist <- { Name = ""; Songs = [] }
                                    else
                                        Console.WriteLine("No se agregó ningún nombre de playlist.")
                                )
                                Button.margin (Thickness(5.0))
                            ]
                        ]
                    ]

                    // Lista de playlists
                    ListBox.create [
                        ListBox.width 300
                        ListBox.height 200
                        ListBox.dataItems lisPlaylists // Asignar la lista de playlists
                        ListBox.onSelectionChanged (fun args ->
                            let selectedItems = args.AddedItems // Obtener los elementos seleccionados
                            if selectedItems.Count > 0 then 
                                let selectedItem = selectedItems.Item(0) // Obtener el primer elemento seleccionado
                                selectedSong <- selectedItem.ToString()
                                Console.WriteLine("Selected song: " + selectedSong)
                            else
                                // No se seleccionó ninguna canción
                                selectedSong <- ""
                                Console.WriteLine("No se seleccionó ninguna canción.")
                        )
                        ListBox.margin (Thickness(0.0, 10.0, 0.0, 10.0))
                    ]

                    // ----------------------------------------------------------- Controles de canciones en playlists -----------------------------------------------------------
                    StackPanel.create [
                        StackPanel.dock Dock.Top
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.horizontalAlignment HorizontalAlignment.Center
                        StackPanel.children [

                            Button.create [  // Botón para agregar canción a playlist
                                Button.width 150
                                Button.content "Add Song to Playlist"
                                Button.onClick (fun _ ->
                                    Console.WriteLine("Selected song: " + selectedSong)
                                    Console.WriteLine("Selected playlist: " + playlist.Name)
                                    
                                    if selectedSong <> "" && selectedPlaylistName <> "" then
                                        Console.WriteLine("Se seleccionó la canción: " + selectedSong + " para agregar a la playlist: " + selectedPlaylistName)
                                        let mutable newPlaylist = []
                                        for playlist in lisPlaylists do
                                            if playlist.Name = selectedPlaylistName then
                                                let updatedPlaylist = { Name = selectedPlaylistName; Songs = playlist.Songs @ [selectedSong] }
                                                newPlaylist <- newPlaylist @ [updatedPlaylist]
                                            else
                                                newPlaylist <- newPlaylist @ [playlist]
                                        lisPlaylists <- newPlaylist
                                        Console.WriteLine("Playlists: " + lisPlaylists.ToString())
                                    else
                                        Console.WriteLine("No se agregó ninguna canción o playlist.")
                                )
                                Button.margin (Thickness(5.0))
                            ]

                            Button.create [  // Botón para eliminar canción de playlist
                                Button.width 150
                                Button.content "Delete Song from Playlist"
                                Button.onClick (fun _ ->
                                    if selectedSong <> "" && selectedPlaylistName <> "" then
                                        Console.WriteLine("Se seleccionó la canción: " + selectedSong + " para eliminar de la playlist: " + selectedPlaylistName)
                                        let mutable newPlaylists = [] // Variable temporal para actualizar la lista de playlists
                                        for playlist in lisPlaylists do
                                            if playlist.Name = selectedPlaylistName then
                                                // Elimina la canción de la lista de reproducción seleccionada
                                                let updatedPlaylist = { Name = selectedPlaylistName; Songs = List.filter (fun s -> s <> selectedSong) playlist.Songs }
                                                newPlaylists <- newPlaylists @ [updatedPlaylist]
                                            else
                                                newPlaylists <- newPlaylists @ [playlist]
                                        lisPlaylists <- newPlaylists // Actualiza la lista de playlists
                                        Console.WriteLine("Playlists: " + lisPlaylists.ToString())
                                    else
                                        Console.WriteLine("No se eliminó ninguna canción o playlist.")
                                )
                                Button.margin (Thickness(5.0))
                            ]

                            Button.create [  // Botón para eliminar playlist
                                Button.width 150
                                Button.content "Delete Playlist"
                                Button.onClick (fun _ ->
                                    if selectedPlaylistName <> "" then
                                        Console.WriteLine("Se seleccionó la playlist: " + selectedPlaylistName + " para eliminar.")
                                        lisPlaylists <- List.filter (fun playlist -> playlist.Name <> selectedPlaylistName) lisPlaylists
                                        Console.WriteLine("Playlists: " + lisPlaylists.ToString())
                                        // Limpia la variable selectedPlaylistName después de eliminar la lista de reproducción
                                        selectedPlaylistName <- ""
                                    else
                                        Console.WriteLine("No se eliminó ninguna lista de reproducción.")
                                )
                                Button.margin (Thickness(5.0))
                            ]
                        ]
                    ]

                    // Lista de canciones filtradas por duración
                    ListBox.create [
                        ListBox.width 300
                        ListBox.height 200
                        ListBox.dataItems songDuration // Asignar la lista de canciones
                        ListBox.onSelectionChanged (fun args ->
                            let selectedItems = args.AddedItems // Obtener los elementos seleccionados
                            if selectedItems.Count > 0 then 
                                let selectedItem = selectedItems.Item(0) // Obtener el primer elemento seleccionado
                                selectedSong <- selectedItem.ToString()
                                Console.WriteLine("Selected song: " + selectedSong)
                            else
                                // No se seleccionó ninguna canción
                                selectedSong <- ""
                                Console.WriteLine("No se seleccionó ninguna canción.")
                        )
                        ListBox.margin (Thickness(0.0, 10.0, 0.0, 10.0))
                    ]

                    // Lista de canciones Top 5
                    ListBox.create [
                        ListBox.width 300
                        ListBox.height 200
                        ListBox.dataItems top5Songs // Asignar la lista de canciones
                        ListBox.onSelectionChanged (fun args ->
                            let selectedItems = args.AddedItems // Obtener los elementos seleccionados
                            if selectedItems.Count > 0 then 
                                let selectedItem = selectedItems.Item(0) // Obtener el primer elemento seleccionado
                                selectedSong <- selectedItem.ToString()
                                Console.WriteLine("Selected song: " + selectedSong)
                            else
                                // No se seleccionó ninguna canción
                                selectedSong <- ""
                                Console.WriteLine("No se seleccionó ninguna canción Top 5.")
                        )
                        ListBox.margin (Thickness(0.0, 10.0, 0.0, 10.0))
                    ]

                    // Lista de canciones de la playlist seleccionada
                    ListBox.create [
                        ListBox.width 300
                        ListBox.height 200
                        ListBox.dataItems playlist.Songs // Asignar la lista de canciones
                        ListBox.onSelectionChanged (fun args ->
                            let selectedItems = args.AddedItems // Obtener los elementos seleccionados
                            if selectedItems.Count > 0 then 
                                let selectedItem = selectedItems.Item(0) // Obtener el primer elemento seleccionado
                                selectedSong <- selectedItem.ToString()
                                Console.WriteLine("Selected song: " + selectedSong)
                            else
                                // No se seleccionó ninguna canción
                                selectedSong <- ""
                                Console.WriteLine("No se seleccionó ninguna canción.")
                        )
                        ListBox.margin (Thickness(0.0, 10.0, 0.0, 10.0))
                    ]
                ]
            ]
        )
        
   
        
   