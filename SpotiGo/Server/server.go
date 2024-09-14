package main

import (
	"bufio"
	"fmt"
	"github.com/dhowden/tag"
	"github.com/hajimehoshi/go-mp3"
	"io"
	"log"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
)

//NOTA: ANTES DE EJECUTAR EL SERVIDOR, SE DEBE CAMBIAR EL PATH DE LA CARPETA DE CANCIONES Y
//EL PATH DEL ARCHIVO DE HISTORIAL DE REPRODUCCIÓN

type Song struct {
	name     string
	genre    string
	artist   string
	duration int64
	filePath string
}

type songList []Song

var songCollection songList
var songsFolder = "Songs"
var songsMutex sync.Mutex

const (
	bufferSize = 1024
)

func main() {
	listener, err := net.Listen("tcp", ":8000")
	if err != nil {
		log.Fatalln(err)
	}
	defer listener.Close()

	fmt.Println("*** SERVER OF SPOTIGO STARTED ***")

	go adminMenu()

	for {
		fmt.Println("Waiting for connections..." + "\n")
		con, err := listener.Accept()
		if err != nil {
			log.Println(err)
			continue
		}

		idClient := +1
		go handleClientRequest(con, idClient)
	}
}

func handleClientRequest(con net.Conn, idClient int) {
	defer func(con net.Conn) {
		err := con.Close()
		if err != nil {
			log.Println(err)
		}
	}(con)

	clientReader := bufio.NewReader(con)

	for {
		clientRequest, err := clientReader.ReadString('\n')

		parts := strings.Fields(clientRequest) // Dividir la cadena en palabras

		infoSong := ""
		if len(parts) > 1 {
			infoSong = strings.Join(parts[1:], " ")
		}

		switch err {
		case nil:
			if clientRequest == "Exit" {
				log.Println("Client requested server to close the connection so closing")
				return
			} else {
				log.Println(" says:", clientRequest)

				clientRequest = strings.TrimSpace(clientRequest)

				fmt.Println("action:", clientRequest)

				command := strings.Split(clientRequest, " ")[0]

				switch command {
				case "1":
					fmt.Println("LLAMANDO A ListSongs")
					go listSongs(con)

				case "2", "5":
					fmt.Println("LLAMANDO A PlaySong")
					fmt.Println("songName:", infoSong)
					go playSong(con, infoSong, idClient, command)
				case "3":
					stopSong(con)
				case "4":
					pauseSong(con)
				case "6":
					fmt.Println("LLAMANDO A SearchSongsByFilter")
					go searchSongsByFilter(con, infoSong)
				case "7":
					fmt.Println("LLAMANDO A TOP 5")
					go searchTop5(con, idClient)
				case "8":
					fmt.Println("LLAMANDO A DURACION")
					go searchSongsByDuration(con, parts[1], parts[2])
				default:
					log.Println("Invalid command.")
				}
			}
		case io.EOF:
			log.Println("Client closed the connection by terminating the process")
			return

		default:
			log.Printf("Error: %v\n", err)
			return
		}

	}
}

func listSongs(con net.Conn) {
	songsMutex.Lock()
	defer songsMutex.Unlock()

	var songs []string
	for _, song := range songCollection {
		songs = append(songs, song.name)
	}

	go func() {
		con.Write([]byte(fmt.Sprintf("%s\n", strings.Join(songs, ","))))
		fmt.Println("Canciones enviadas")
	}()

}

func playSong(con net.Conn, songName string, idClient int, command string) {
	songsMutex.Lock()
	defer songsMutex.Unlock()

	fmt.Println("Reproduciendo canción:", songName)

	for _, song := range songCollection {
		if song.name == songName {
			err := sendDecodedSongToClient(con, song, idClient, command)
			if err != nil {
				log.Println(err)
			}
			con.Close()
			return
		}
	}
}

func stopSong(con net.Conn) {
	con.Write([]byte("Stop\n")) // Envia la señal de detener la reproducción al cliente
	fmt.Println("Canción detenida")
}

func pauseSong(con net.Conn) {
	con.Write([]byte("Pause\n")) // Envia la señal de pausar la reproducción al cliente
	fmt.Println("Canción pausada")
}

func searchSongsByFilter(con net.Conn, filter string) {
	songsMutex.Lock()
	defer songsMutex.Unlock()

	var filteredSongs []string
	for _, song := range songCollection {
		if strings.Contains(song.name, filter) || strings.Contains(song.artist, filter) || strings.Contains(song.genre, filter) {
			filteredSongs = append(filteredSongs, song.name)
		}
	}

	con.Write([]byte(fmt.Sprintf("%s\n", strings.Join(filteredSongs, ","))))
}

func adminMenu() {
	directory := "C:\\Users\\Karina\\Desktop\\Proyectos\\PROYECTO GoFSharp\\SongsToPlayer"
	err := loadSongsFromDirectory(directory)
	if err != nil {
		log.Println(err)
		return
	}

	consoleScanner := bufio.NewScanner(os.Stdin)

	for {
		fmt.Println("\nAdmin Options:")
		fmt.Println("1. List Songs")
		fmt.Println("2. Add Song")
		fmt.Println("3. Remove Song")
		fmt.Println("4. Exit Admin Mode")
		fmt.Print("Select an admin option: ")

		consoleScanner.Scan()
		adminOption := consoleScanner.Text()

		switch adminOption {
		case "1":
			showListSongs()
		case "2":
			fmt.Print("Enter the path of the MP3 file: ")
			consoleScanner.Scan()
			filePath := consoleScanner.Text()
			addSong(filePath)
		case "3":
			removeSong()
		case "4":
			fmt.Println("Exiting admin mode...")
			return
		default:
			fmt.Println("Invalid admin option.")
		}
	}
}

func sendDecodedSongToClient(conn net.Conn, song Song, idClient int, command string) error {
	file, err := os.Open(song.filePath)
	if err != nil {
		return err
	}
	defer file.Close()

	_, fileName := filepath.Split(song.filePath)

	fmt.Println("Enviando archivo:", fileName)

	_, err = io.Copy(conn, file)
	if err != nil {
		fmt.Println("Error al enviar el archivo:", err)
	} else {
		if command == "2" { // Si el comando es 2, es porque se está reproduciendo una canción, por lo que se debe guardar en el historial de reproducción
			// Verificar si el archivo de historial de reproducción existe
			historyFilePath := "C:\\Users\\Karina\\Desktop\\Proyectos\\PROYECTO GoFSharp\\SpotiGo\\Server\\playHistory.txt"
			if _, err := os.Stat(historyFilePath); os.IsNotExist(err) {
				// Si no existe, crearlo
				file, err := os.Create(historyFilePath)
				if err != nil {
					log.Println("Error al crear el archivo de historial:", err)
				}
				file.Close()
			}

			// Abrir el archivo de historial de reproducción para escribir
			file, err := os.OpenFile(historyFilePath, os.O_APPEND|os.O_WRONLY, 0644)
			if err != nil {
				log.Println("Error al abrir el archivo de historial:", err)
			} else {
				// Escribir el registro en el archivo de historial
				_, err = file.WriteString(strconv.Itoa(idClient) + " " + song.name + "\n")
				if err != nil {
					log.Println("Error al escribir en el archivo de historial:", err)
				} else {
					fmt.Println("Archivo enviado con éxito:", fileName)
				}
				file.Close()
			}
		}
	}

	return err
}

func showListSongs() {
	songsMutex.Lock()
	defer songsMutex.Unlock()

	fmt.Println("\nSongs:")
	for _, song := range songCollection {
		fmt.Println("~~~ " + song.name)
	}
}

func loadSongsFromDirectory(pathDirectory string) error {
	if _, err := os.Stat(songsFolder); os.IsNotExist(err) {
		os.Mkdir(songsFolder, os.ModeDir)
	}

	err := filepath.Walk(pathDirectory, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if !info.IsDir() && strings.HasSuffix(info.Name(), ".mp3") {
			addSong(path)
		}
		return nil
	})

	if err != nil {
		return err
	}

	return nil
}

func addSong(filePath string) {
	fileInfo, err := os.Stat(filePath)
	if err != nil {
		log.Println(err)
		return
	}

	if fileInfo.IsDir() || filepath.Ext(filePath) != ".mp3" {
		fmt.Println("The file is not an MP3 file.")
		return
	}

	file, err := os.Open(filePath)
	if err != nil {
		log.Println(err)
		return
	}
	defer file.Close()

	metadata, err := tag.ReadFrom(file)

	fmt.Println("Song name:", metadata.Title())
	if err != nil {
		log.Println(err)
		return
	}

	// SE EXTRAE LA DURACION LA CANCIÓN
	decoder, err := mp3.NewDecoder(file)
	if err != nil {
		log.Println(err)
		return
	}

	instance := decoder.Length() / 4
	duration := instance / int64(decoder.SampleRate()) // DURACIÓN DE LA CANCIÓN EN SEGUNDOS

	song := Song{
		name:     metadata.Title(),
		genre:    metadata.Genre(),
		artist:   metadata.Artist(),
		duration: duration,
		filePath: filePath,
	}

	fileName := filepath.Base(filePath)
	destPath := filepath.Join(songsFolder, fileName)
	copyFile(filePath, destPath)

	songCollection = append(songCollection, song)
	fmt.Println("Song added successfully.")
}

func removeSong() {
	showListSongs()
	fmt.Print("Enter the name of the song to remove: ")
	consoleScanner := bufio.NewScanner(os.Stdin)
	consoleScanner.Scan()
	songName := consoleScanner.Text()

	for i, song := range songCollection {
		if song.name == songName {
			fileName := filepath.Base(song.filePath)
			filePath := filepath.Join(songsFolder, fileName)
			err := os.Remove(filePath)
			if err != nil {
				log.Println(err)
			}

			songCollection = append(songCollection[:i], songCollection[i+1:]...)
			fmt.Println("Song removed successfully.")
			return
		}
	}

	fmt.Println("Song not found.")
}

func copyFile(src, dest string) error {
	sourceFile, err := os.Open(src)
	if err != nil {
		return err
	}
	defer sourceFile.Close()

	destFile, err := os.Create(dest)
	if err != nil {
		return err
	}
	defer destFile.Close()

	_, err = io.Copy(destFile, sourceFile)
	return err
}

// ------------------ CRITERIOS DE BUSQUEDA ------------------//

// BUSCA LAS 5 CANCIONES MÁS ESCUCHADAS POR EL USUARIO EN EL ARCHIVO DE HISTORIAL DE REPRODUCCIÓN
func searchTop5(con net.Conn, idClient int) {

	//SE CREA UN MAPA PARA CONTAR EL NÚMERO DE VECES QUE SE HA ESCUCHADO UNA CANCIÓN
	songsMap := make(map[string]int)

	//SE ABRE EL ARCHIVO DE HISTORIAL DE REPRODUCCIÓN
	file, err := os.Open("C:\\Users\\Karina\\Desktop\\Proyectos\\PROYECTO GoFSharp\\SpotiGo\\Server\\playHistory.txt")
	if err != nil {
		log.Println(err)
	}
	defer file.Close()

	//SE RECORRE EL ARCHIVO DE HISTORIAL DE REPRODUCCIÓN
	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		line := scanner.Text()
		words := strings.Split(line, " ") // Dividir la cadena en palabras

		id, err := strconv.Atoi(words[0]) // Se obtiene el id del cliente
		if err != nil {
			log.Println(err)
		}
		if id == idClient { // Si el id del cliente es igual al id del cliente que está solicitando las canciones más escuchadas, se cuenta la canción

			songName := strings.Join(words[1:], " ")
			songsMap[songName] += 1
		}

	}

	fmt.Println(songsMap)

	//SE ORDENA EL MAPA DE CANCIONES DE MAYOR A MENOR SEGÚN EL NÚMERO DE VECES QUE SE HA ESCUCHADO
	var result []string
	for i := 0; i < 5; i++ {
		max := 0
		var songName string
		for key, value := range songsMap {
			if value > max {
				max = value
				songName = key
			}
		}
		result = append(result, songName)
		delete(songsMap, songName)
	}

	fmt.Println(result)
	//con.Write([]byte(fmt.Sprintf("%s\n", strings.Join(result, ","))))
	fmt.Fprintf(con, "%s\n", strings.Join(result, ","))
}

// RECORRE LA LISTA DE CANCIONES Y BUSCA LAS CANCIONES QUE DUREN UN RANGO DE TIEMPO
func searchSongsByDuration(con net.Conn, minDuration string, maxDuration string) {
	songsMutex.Lock()
	defer songsMutex.Unlock()

	fmt.Println("minDuration:", minDuration)
	fmt.Println("maxDuration:", maxDuration)

	//PARA CONVERTIR LOS STRINGS A INT64
	minDurationInt, err := strconv.Atoi(minDuration)
	if err != nil {
		log.Println(err)
	}

	maxDurationInt, err := strconv.Atoi(maxDuration)
	if err != nil {
		log.Println(err)
	}

	//minutos a segundos
	minDurationInt = minDurationInt * 60
	maxDurationInt = maxDurationInt * 60

	fmt.Println("minDuration:", int64(minDurationInt))
	fmt.Println("maxDuration:", int64(maxDurationInt))

	var filteredSongs []string
	for _, song := range songCollection {
		fmt.Println("song.duration:", song.duration)
		if song.duration >= int64(minDurationInt) && song.duration <= int64(maxDurationInt) {

			filteredSongs = append(filteredSongs, song.name)
		}
	}

	fmt.Println("filteredSongs:", filteredSongs)
	fmt.Fprintf(con, "%s\n", strings.Join(filteredSongs, ","))

}
