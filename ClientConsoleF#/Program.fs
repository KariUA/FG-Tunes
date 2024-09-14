
open System
open System.IO
open System.Net.Sockets
open System.Threading.Tasks
open System.Threading
open NAudio.Wave

let serverHost = "localhost"
let serverPort = 8000

let trim (str: string) =
    str.Trim()

let mutable isPaused = false 
let mutable isStopped = false
let mutable currentSongName = ""
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
let playSongInBackground (serverStream: NetworkStream, songBytes: byte[], resumeFrom: int64 option) =
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
                    Console.WriteLine($"Error durante la reproducción: {ex.Message}")
        else
            Console.WriteLine("Los datos no parecen ser un archivo MP3 válido.")
    )

let asyncReadFromServer (reader : StreamReader) =
    async {
        while true do
            let! response = Async.AwaitTask (reader.ReadLineAsync())
            if response = null then
                return ()
            else
                Console.WriteLine(response)
    }

let asyncListSongs (writer : StreamWriter, serverStream : NetworkStream) =
    async {
        try
            let command = "1" 
            writer.WriteLine(command)
            writer.Flush()

            use serverReader = new StreamReader(serverStream)
            let response = serverReader.ReadLine()

            if not (String.IsNullOrWhiteSpace(response)) then
                Console.WriteLine("Songs:")
                response.Split([|','|], StringSplitOptions.RemoveEmptyEntries)
                |> Array.iter (fun song -> Console.WriteLine("~~~ " + song))
            else
                Console.WriteLine("No se encontraron canciones en el servidor.")
        with
            | :? Exception as ex ->
                Console.WriteLine($"Error al listar canciones: {ex.Message}")
    }

let asyncPlaySong (writer : StreamWriter, serverStream : NetworkStream, songName : string, numCommand : string, resumeFrom: int64 option) =
    async {
        
        isStopped <- false 
        isPaused <- false
        currentSongName <- songName
        
        let command = sprintf "%s %s" numCommand songName // Comando para reproducir una canción específica
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
        playSongInBackground (serverStream, songBytes, resumeFrom)
    }

let asyncStopSong (writer : StreamWriter, serverStream : NetworkStream) =
    async {
        try
            isStopped <- true 
            let command = "3" 
            writer.WriteLine(command)
            writer.Flush()      
        with
            | :? Exception as ex ->
                Console.WriteLine($"Error al detener la reproducción: {ex.Message}")
    }

let asyncPauseSong (writer : StreamWriter, serverStream : NetworkStream) =
    async {
        try
            isPaused <- true 
            let command = "4" 
            writer.WriteLine(command)
            writer.Flush()      
        with
            | :? Exception as ex ->
                Console.WriteLine($"Error al detener la reproducción: {ex.Message}")
    }

let asyncResumeSong (writer : StreamWriter, serverStream : NetworkStream) =
    async {
        try
            isPaused <- false
            match currentPlaybackThread with
            | Some(thread) ->
                // Reanudar desde el punto de pausa
                let resumePosition = currentPositionMs
                asyncPlaySong (writer, serverStream, currentSongName, "5", Some(resumePosition)) |> Async.StartAsTask |> ignore
            | None ->
                Console.WriteLine("No se puede reanudar, no hay una canción en pausa.")
        with
            | :? Exception as ex ->
                Console.WriteLine($"Error al detener la reproducción: {ex.Message}")
    }

let asyncSearchSong (writer : StreamWriter, serverStream : NetworkStream, filter : string) =
    async {
        try
            let command = sprintf "6 %s" filter // Comando para buscar canciones por filtro
            writer.WriteLine(command)
            writer.Flush()

            use serverReader = new StreamReader(serverStream)
            let response = serverReader.ReadLine()

            if not (String.IsNullOrWhiteSpace(response)) then
                Console.WriteLine("Songs:")
                response.Split([|','|], StringSplitOptions.RemoveEmptyEntries)
                |> Array.iter (fun song -> Console.WriteLine("~~~ " + song))
            else
                Console.WriteLine("No se encontraron canciones en el servidor.")
        with
            | :? Exception as ex ->
                Console.WriteLine($"Error al listar canciones: {ex.Message}")
    }
  
let asyncSearchTop5 (writer : StreamWriter, serverStream : NetworkStream) =
    async {
        try
            let command = "7" // Comando para buscar las 5 canciones más escuchadas
            writer.WriteLine(command)
            writer.Flush()

            use serverReader = new StreamReader(serverStream)
            let response = serverReader.ReadLine()

            if not (String.IsNullOrWhiteSpace(response)) then
                Console.WriteLine("Songs:")
                response.Split([|','|], StringSplitOptions.RemoveEmptyEntries)
                |> Array.iter (fun song -> Console.WriteLine("~~~ " + song))
            else
                Console.WriteLine("No se encontraron canciones en el servidor.")
        with
            | :? Exception as ex ->
                Console.WriteLine($"Error al listar canciones: {ex.Message}")
    }
    
let asyncSearchByDuration (writer : StreamWriter, serverStream : NetworkStream, min : string, max : string) =
    async {
        try
            let command = sprintf "8 %s %s" min max // Comando para buscar canciones por duración
            writer.WriteLine(command)
            writer.Flush()

            use serverReader = new StreamReader(serverStream)
            let response = serverReader.ReadLine()

            if not (String.IsNullOrWhiteSpace(response)) then
                Console.WriteLine("\nSongs with duration between " + min + " and " + max + " minutes:")
                
                response.Split([|','|], StringSplitOptions.RemoveEmptyEntries)
                |> Array.iter (fun song -> Console.WriteLine("~~~ " + song))
            else
                Console.WriteLine("No se encontraron canciones en el servidor.")
        with
            | :? Exception as ex ->
                Console.WriteLine($"Error al listar canciones: {ex.Message}")
    }

let rec menuLoop (writer : StreamWriter, serverStream : NetworkStream, playlists : Playlist list) =
    Console.Clear()
    let client = new TcpClient(serverHost, serverPort)
    use serverStream = client.GetStream()
    use writer = new StreamWriter(serverStream)
    Console.WriteLine("Menú:")
    Console.WriteLine("1. Ver lista de canciones")
    Console.WriteLine("2. Reproducir una canción")
    Console.WriteLine("3. Detener una canción")
    Console.WriteLine("4. Pausar canción")
    Console.WriteLine("5. Reanudar canción")
    Console.WriteLine("6. Buscar canción por filtro (nombre, artista, álbum)")
    Console.WriteLine("7. Buscar 5 canciones más escuchadas")
    Console.WriteLine("8. Buscar por duración")
    Console.WriteLine("9. Crear playlist")
    Console.WriteLine("10. Agregar canción a playlist")
    Console.WriteLine("11. Eliminar canción de playlist")
    Console.WriteLine("12. Reproducir playlist")
    Console.WriteLine("13. Eliminar playlist")
    Console.WriteLine("14. Ver listas de playlist")
    Console.WriteLine("15. Adelantar canción 5 segundos")
    Console.WriteLine("16. Retroceder canción 5 segundos")
    Console.WriteLine("0. Salir")
    Console.Write("Elija una opción: ")

    let choice = Console.ReadLine() |> trim
    match choice with
    
    | "1" -> // Listar canciones
        asyncListSongs (writer, serverStream) |> Async.StartAsTask |> ignore
        System.Threading.Thread.Sleep(5000)
        menuLoop (writer, serverStream, playlists)
        
    | "2" -> // Reproducir canción  
        Console.Write("Canción: ")
        let songName = Console.ReadLine() |> trim
        currentSongName <- songName
        asyncPlaySong ( writer, serverStream, songName, "2", None) |> Async.StartAsTask |> ignore
        System.Threading.Thread.Sleep(2000)
        menuLoop (writer, serverStream, playlists)
        
    | "3" -> // Detener canción
        asyncStopSong (writer, serverStream) |> Async.StartAsTask |> ignore
        menuLoop (writer, serverStream, playlists)
        
    | "4" ->  // Pausar canción
        asyncPauseSong (writer, serverStream) |> Async.StartAsTask |> ignore
        menuLoop (writer, serverStream, playlists)
        
    | "5" -> // Reanudar canción
        asyncResumeSong (writer, serverStream) |> Async.StartAsTask |> ignore
        System.Threading.Thread.Sleep(2000)
        menuLoop (writer, serverStream, playlists)
    
    | "6" -> // Buscar canción por filtro
        Console.Write("Filtro: ")
        let filter = Console.ReadLine() |> trim
        asyncSearchSong (writer, serverStream, filter) |> Async.StartAsTask |> ignore
        menuLoop (writer, serverStream, playlists)
        
    | "7" -> // Buscar 5 canciones más escuchadas
        asyncSearchTop5 (writer, serverStream) |> Async.StartAsTask |> ignore
        menuLoop (writer, serverStream, playlists)
    
    | "8" ->  // Buscar canciones por duración
        Console.Write("Duración mínima (min): ")
        let durMin = Console.ReadLine() |> trim
        Console.Write("Duración máxima (min): ")
        let durMax = Console.ReadLine() |> trim
        asyncSearchByDuration (writer, serverStream, durMin, durMax) |> Async.StartAsTask |> ignore
        menuLoop (writer, serverStream, playlists)
        
    | "9" -> // Crear playlist
        Console.Write("Nombre de la nueva lista de reproducción: ")
        let playlistName = Console.ReadLine() |> trim
        let newPlaylist = { Name = playlistName; Songs = [] }
        menuLoop (writer, serverStream, newPlaylist :: playlists)
        
    | "10" -> // Agregar canción a playlist
        Console.Write("Nombre de la lista de reproducción: ")
        let playlistName = Console.ReadLine() |> trim
        Console.Write("Nombre de la canción a agregar: ")
        let songName = Console.ReadLine() |> trim
        match List.tryFind (fun playlist -> playlist.Name = playlistName) playlists with
        | Some(playlist) ->
            if not (List.exists (fun song -> song = songName) playlist.Songs) then
                let updatedPlaylist = { playlist with Songs = songName :: playlist.Songs }
                menuLoop (writer, serverStream, updatedPlaylist :: List.filter (fun p -> p.Name <> playlist.Name) playlists)
            else
                Console.WriteLine("La canción ya está en la lista de reproducción.")
                System.Threading.Thread.Sleep(1000)
                menuLoop (writer, serverStream, playlists)
        | None ->
            Console.WriteLine("La lista de reproducción no se encontró.")
            System.Threading.Thread.Sleep(1000)
            menuLoop (writer, serverStream, playlists)
        
    | "11" -> // Eliminar canción de playlist
        Console.Write("Nombre de la lista de reproducción: ")
        let playlistName = Console.ReadLine() |> trim
        Console.Write("Nombre de la canción a eliminar: ")
        let songName = Console.ReadLine() |> trim
        match List.tryFind (fun playlist -> playlist.Name = playlistName) playlists with
        | Some(playlist) ->
            let updatedSongs = List.filter (fun song -> song <> songName) playlist.Songs
            let updatedPlaylist = { playlist with Songs = updatedSongs }
            menuLoop (writer, serverStream, updatedPlaylist :: List.filter (fun p -> p.Name <> playlist.Name) playlists)
        | None ->
            Console.WriteLine("La lista de reproducción no se encontró.")
            System.Threading.Thread.Sleep(1000)
            menuLoop (writer, serverStream, playlists)
        
    | "12" -> // Reproducir playlist
        Console.Write("Nombre de la lista de reproducción a reproducir: ")
        let playlistName = Console.ReadLine() |> trim
        match List.tryFind (fun playlist -> playlist.Name = playlistName) playlists with
        | Some(playlist) ->
            let rec playPlaylist = function
                | [] -> ()
                | song::rest ->
                    asyncPlaySong (writer, serverStream, song, "2", None) |> Async.StartAsTask |> ignore
                    System.Threading.Thread.Sleep(2000)
                    playPlaylist rest
            playPlaylist playlist.Songs
            menuLoop (writer, serverStream, playlists)
        | None ->
            Console.WriteLine("La lista de reproducción no se encontró.")
            System.Threading.Thread.Sleep(1000)
            menuLoop (writer, serverStream, playlists)
        
    | "13" -> // Eliminar playlist
        Console.Write("Nombre de la lista de reproducción a eliminar: ")
        let playlistName = Console.ReadLine() |> trim
        match List.tryFind (fun playlist -> playlist.Name = playlistName) playlists with
        | Some(playlist) ->
            menuLoop (writer, serverStream, List.filter (fun p -> p.Name <> playlist.Name) playlists)
        | None ->
            Console.WriteLine("La lista de reproducción no se encontró.")
            System.Threading.Thread.Sleep(1000)
            menuLoop (writer, serverStream, playlists)
        
    | "14" -> // Ver listas de playlist
            Console.WriteLine("Listas de reproducción:")
            playlists
            |> List.iter (fun playlist -> Console.WriteLine("~~~ " + playlist.Name))
            System.Threading.Thread.Sleep(5000)
            menuLoop (writer, serverStream, playlists)
        
    | "15" -> // Adelantar canción 5 segundos
        advanceSongBy5Seconds ()
        menuLoop (writer, serverStream, playlists)
        
    | "16" -> // Retroceder canción 5 segundos
        rewindSongBy5Seconds ()
        menuLoop (writer, serverStream, playlists)
        
    | "0" ->
        // Salir del bucle
        ()
    | _ ->
        Console.WriteLine("Opción no válida. Intente de nuevo.")
        System.Threading.Thread.Sleep(1000)
        menuLoop (writer, serverStream, playlists)

[<EntryPoint>]
let main argv =
    Console.WriteLine("\nBienvenido al GoSharp Player!\n")

    let client = new TcpClient(serverHost, serverPort)
    use serverStream = client.GetStream()
    use writer = new StreamWriter(serverStream)
    use reader = new StreamReader(serverStream)

    // Leer las canciones disponibles del servidor
    let listTask = asyncListSongs (writer, serverStream) |> Async.StartAsTask

    // Iniciar la interfaz de usuario
    menuLoop (writer, serverStream, [])

    // Esperar a que se complete la tarea de listar canciones
    listTask.Wait()

    // Cerrar la conexión del cliente
    serverStream.Close()

    0 // Salir del programa