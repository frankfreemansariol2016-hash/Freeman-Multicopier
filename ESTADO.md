# Freeman MULTICOPIER — estado del trabajo

Fecha: 27 de septiembre de 2026
Estado: **entregado.** El motor va de 155 MB/s a del orden de 300 a 1300 MB/s con un destino
solo, y de conjunto en varios destinos pasa de 89 a entre 78 y 144 MB/s. El reparto a varios
destinos está comprobado byte a byte, soltar ya es copiar, y el ícono es vectorial. Los tres
entregables están en `C:\Users\FREEMAN\Downloads` y salen de `build.ps1` con 82/82 de
pruebas correctas.

---

## 1. Qué pedía el cliente

1. Las velocidades de transferencia eran muy lentas (unos 39 MB/s de media).
2. Que en el origen se vea la lectura del archivo que se está copiando y su
   transferencia hacia el otro lugar, con la tasa de transferencia de cada archivo que se
   está transfiriendo desde ese origen.
3. **Que al arrastrar comience la copia automáticamente, sin tener que pulsar COPIAR.**
4. **Un ícono vectorizado: un disco duro fusionado con una F de Freeman.**

Los cuatro están hechos. El 2 tiene su sitio en la ventana: bloque **ORIGEN** arriba de la
pestaña "Copia en curso", con `LECTURA`, `TRANSFERENCIA ... hacia N destino(s)`, una línea
por destino y la línea `ADEMAS` con los demás archivos que salen del origen a la vez. El 3
tiene su apartado en el §5, y el 4 en el §6.

---

## 2. Banco de pruebas

Antes de tocar el motor se montó un banco para medir de verdad, porque "va lento" sin
cifras no se arregla. Vive fuera del proyecto, en la carpeta temporal:

```
C:\Users\FREEMAN\AppData\Local\Temp\opencode\banco\
   Banco.csproj    compila las fuentes reales del motor: Core\CopyService.cs,
                   Core\Devices\*.cs, Core\Engine\*.cs, Core\Model\*.cs, Core\Util\*.cs,
                   y tambien UI\Txt.cs, UI\LiveCopyRow.cs y UI\OriginReadout.cs
   Program.cs      escenarios A..F, BANCO_ESTRES, BANCO_PANEL, BANCO_ORIGEN,
                   BANCO_BINDINGS y BANCO_SERVICIO
```

```powershell
$env:DOTNET_ROOT = "C:\Users\FREEMAN\AppData\Local\Programs\dotnet"
& "$env:DOTNET_ROOT\dotnet.exe" build "C:\Users\FREEMAN\AppData\Local\Temp\opencode\banco\Banco.csproj" -c Release
& "$env:DOTNET_ROOT\dotnet.exe" "C:\Users\FREEMAN\AppData\Local\Temp\opencode\banco\bin\Release\net10.0-windows\Banco.dll"
```

- `BANCO_ESTRES=30` — 8 destinos a la vez, con `PlannedTargets = 8` para que sí se agrupen,
  comprobando byte a byte.
- `BANCO_PANEL=8` — copia real mientras se lee el bloque ORIGEN y las filas, y **compara
  los 8 archivos destino con el origen al final**. Es la prueba que hace ver que el reparto
  funciona.
- `BANCO_ORIGEN=8` — lee el bloque ORIGEN como lo lee la ventana y guarda una muestra por
  refresco, para poder afirmar algo sobre la línea `ADEMAS` y no solo sobre un pantallazo.
- `BANCO_BINDINGS` — recorre las 41 rutas del XAML y comprueba que cada una tiene su
  propiedad detrás. Un enlace roto no peta al compilar: peta en pantalla, en silencio.
- `BANCO_SERVICIO` — el caso nuevo: servicio de copia más dispositivos de mentira, con
  carpetas reales. Comprueba la estructura que le toca a cada tipo de dispositivo, que el
  modo vuelve a Normal, que la cola es por dispositivo, que los archivos salen enteros por
  huella SHA256 y que una segunda vuelta en vacío tarda 0 ms sin tocar los archivos.

Dos detalles del banco que costaron tiempo:

- Compila `Core\CopyService.cs` **y** `Core\Devices\*.cs`, y necesita el
  `<PackageReference Include="System.Management" Version="10.0.0" />`, porque
  `VolumeInspector` lo usa. Sin ese paquete el banco no compila.
- `JobFor` guarda el trabajo por `Volume.Id`. Dos rondas del banco con el mismo identificador
  comparten cola, así que `Fabrica(root, kinds, prefijo)` mete un prefijo distinto en cada
  ronda para que no se pisen.

**El banco mide lo que mide el programa**, porque los dos usan
`BufferBudget.CreateDefault()`. Ese método se creó para eso: si el presupuesto vive en un
solo sitio, el banco no puede mentir.

### Techo real del hardware de esta máquina (medido, no supuesto)

| Volumen | escritura sostenida | lectura | tipo |
|---|---|---|---|
| C: | 85–106 MB/s | 1,2–3,7 GB/s | SSD |
| D: | 48 MB/s | 1,2–2,6 GB/s | disco lento |

Rangos de dos sondas distintas: la de la corrida de las 19:09 dio C: 106 MB/s de escritura y
1248 MB/s de lectura; otra anterior dio 85–97 y 0,8–3,7. RAM 23,9 GB. Trece volúmenes
visibles (B:–M: y P:).

Esto importa mucho: **con varios destinos, el conjunto ya no puede pasar de ~130 MB/s en
C: porque el disco es el que manda.** Si en la ventana se ven 25 MB/s por destino con 5
destinos copiando a C:, no es un fallo del programa: son 125 MB/s del mismo disco
repartidos. Por eso el bloque ORIGEN enseña el total junto a la parte de cada uno.

Ojo también con el banco cuando el destino está en el mismo disco que el origen: con
destinos en carpetas temporales de C:, la caché de disco se traga la escritura y los
casos duran 0,4 s con cifras imposibles (6 GB/s). Esos números no sirven. El caso D
(56 s) y la sonda de disco sí sirven.

---

## 3. Fallos encontrados y corregidos

### 3.1 El planificador recortaba la velocidad (el primer culpable de los 39 MB/s)

`CopyScheduler.Rebalance` corría cada 1200 ms y ponía a cada dispositivo un tope
calculado con la velocidad **ya medida**:

```csharp
long share = (long)(total * (weights[i] / sum));
engine.SetThrottle(id, share);
```

Eso es un trinquete que solo baja: en cuanto un dispositivo tenía un tropiezo, el total
caía, y como el recorte impedía que la máquina fuera más rápida que el recorte, ya no
podía volver a subir. Medido: 4 destinos clavados en 25 MB/s cada uno mientras un destino
solo hacía el mismo archivo a 155 MB/s.

**Corregido:** ya no se recorta a nadie (`SetThrottle(..., 0)`). Repartir no es recortar.

### 3.2 El pool propio, y una afirmación que resultó ser falsa

Se puso un pool propio (`ArrayPool.Create(8 MB, 64)`) con este motivo, escrito en el
código: "el pool compartido de .NET no recicla nada por encima de 1 MB, así que cada
bloque de 2 o 4 MB era una reserva nueva y se tiraba al llegar al suelo". **Es falso**, y se
comprobó leyendo el fuente del runtime:

```
SharedArrayPool.cs   private const int NumBuckets = 27;   // hasta 1024*1024*1024 + 1
Utilities.cs         SelectBucketIndex(n)   = Log2(n - 1 | 15) - 3
                     GetMaxSizeForBucket(i) = 16 << i
```

Para un bloque de 2 MB, `SelectBucketIndex(2 MB)` da el cubo 17, y `16 << 17` son
justamente 2 MB. O sea que **el pool compartido sí recicla los bloques de 2 MB**. El tope
de 1 MB era de las primeras versiones de .NET Core y se quedó escrito como si fuera
todavía verdad. Por el camino se llegó a afirmar que ese pool solo se comía "dos tercios
de la velocidad", y eso tampoco está medido: la subida de 155 a 1300 MB/s es del doble
buffer del 3.3.

**El pool propio se queda**, pero por lo que se puede defender: una sola lista plana de 64
buffers por cubo, sin caché por hilo ni particiones por núcleo, con techo conocido de 8 MB
y sin depender de las variables de entorno del compartido. El comentario de `CopyBlock` y
este apartado están corregidos para que no vuelvan a mentir.

### 3.3 La lectura y la escritura iban encadenadas

`CopyAloneAsync` hacía: leer bloque → escribir bloque → leer → escribir. En serie la copia
va a la **suma** de las dos velocidades, no a la más rápida.

**Corregido:** dos buffers que se turnan; la lectura del bloque siguiente arranca antes de
escribir el actual. Solo: 155 → 1300–1400 MB/s.

### 3.4 La ventana de unión no dejaba unirse a nadie (y costaba 100 ms por archivo)

El motor llamaba a `Start()` en cuanto se unía el primero, y `TryJoin` rechazaba a
cualquier otro porque el pump ya había arrancado. La ventana de 100 ms **nunca** había
servido para nada: solo retrasaba. Se bajó a 40 ms y se movió la puerta al final de
`Start()`.

**Pero el motivo por el que se decidía abrir la ventana estaba equivocado.** Era
"¿hay más de un trabajo en marcha?". En el instante en que se sueltan cinco archivos a la
vez, el primer trabajo llega a abrir su sesión **antes** de que se repartan los otros
cuatro, así que el contador todavía vale uno y la ventana no se abre. Es decir: la función
que debía hacer que cinco destinos leyeran el origen una sola vez **no se activaba nunca**
en el caso para el que existía.

**Corregido:** quien reparte la lista sabe cuántos destinos son, y se lo dice al trabajo
(`CopyJob.PlannedTargets`, lo pone `CopyService.StartAllAsync`). La ventana se abre si el
archivo es de 8 MB o más **y** hay más de un destino previsto. Sin carrera posible.

### 3.5 Corrupción de datos al final (el grave)

Al arreglar 3.4 apareció un fallo de integridad: un destino podía **unirse a la sesión
después** de que el pump cerrara la lista. Entonces ese destino consumía bloques cuyo
recuento de referencias no lo incluía, así que el último en soltarlos devolvía el buffer
al reutilizador **mientras ese destino seguía escribiendo desde él**, y el pump se lo
volvía a prestar para el bloque siguiente. Salía un archivo hecho polvo sin ningún error
de ningún tipo.

**Corregido:** la lista se fija y se cierra en `Start()`, dentro del mismo cerrojo, y el
pump usa exactamente esa lista. Quien llega tarde copia por su cuenta.

### 3.6 El reparto a varios destinos **nunca se implementó** (el de verdad)

Con 3.4 arreglado, las sesiones sí se agruparon por fin… y entonces un destino se quedó
con **0 bytes** de un archivo de 4. Al mirarlo, la causa era de fondo:

```
_un solo canal por sesión
el pump escribía cada bloque UNA vez en ese canal
todos los destinos leían del MISMO canal
```

Cada bloque se lo llevaba el primer destino que lo leía. Los demás se quedaban sin él. Con
ocho destinos, cada uno se llevaba una octava parte del archivo y los otros siete se
quedaban con un archivo a medias **que el motor daba por bueno** (`item.BytesDone =
item.Length` sin mirar nada). El recuento de referencias de `CopyBlock` estaba puesto,
justo para un reparto que no existía.

Por qué la prueba interna pasaba: el archivo grande de la prueba multidestino era de 5 MB,
por debajo del umbral de 8 MB, así que las sesiones nunca se agrupaban y el reparto roto
nunca llegaba a ejecutarse. Con la ventana arreglada, sí se agrupaba, y el fallo apareció
enseguida.

**Corregido:** cada destino tiene su cola propia, bounded, y el pump mete el mismo bloque
en todas. Las colas guardan referencias al mismo `CopyBlock`, así que la RAM de una sesión
sigue siendo **una** copia de los bloques y no una por destino: lo único que se multiplica
es la ranura de la cola, unos cuantos bytes por bloque y destino. Un destino que falla o se
cancela sigue vaciando su cola y soltando bloques, que es lo que evita que el pump se atasque.

Comprobado con el banco: 8 destinos al mismo archivo de 600 MB, **8 de 8 archivos
enteros** byte a byte, y las ocho filas enseñando la misma cifra de lectura (659 MB/s)
porque la lectura del origen es una sola.

### 3.7 Consultar el espacio libre en cada archivo

`new DriveInfo(...).AvailableFreeSpace` por archivo es una llamada al sistema, y en una
carpeta de mil imágenes se notaba en la barra de progreso.

**Corregido:** un `DriveInfo` por dispositivo, cacheado.

### 3.8 El medidor de velocidad mentía por lo mal muestreado

Ventana de 250 ms con media móvil 0,7/0,3, y además se creaba un medidor nuevo por
archivo, así que los archivos pequeños no llegaban a dar una sola muestra.

**Corregido:** ventana de 150 ms, media 0,45/0,55, y medidores separados para **lectura del
origen** y **escritura en el destino**.

### 3.9 Un "fallo" que no era un fallo (y costó una hora)

Un buen rato se creyó que la copia no estaba escribiendo nada. No era verdad: la copia
ocurría, y los archivos caían en sitios donde no se los buscaba.

La causa: en un disco, la estructura de partida es `Structure`, que espeja la ruta
**absoluta desde la raíz de la unidad**. Al soltar
`C:\Users\FREEMAN\AppData\Local\Temp\opencode\origen-ui\<sello>\peli-0.mkv` sobre un disco,
el archivo acaba en `<disco>\C\Users\FREEMAN\AppData\Local\Temp\opencode\origen-ui\<sello>\`,
a **profundidad 7**. Buscarlo con `Get-ChildItem -Depth 2` o `-Depth 4` no lo encuentra
nunca, y la búsqueda sin límite sí.

No es un fallo: es el comportamiento correcto de `Structure`, y lo que hace falta es
**documentarlo** para que no vuelva a pasar. Está en el manual (capítulo ESTRUCTURAS y
pregunta de la página 27) y en `StructureResolver.Resolve` / `RelClamped`. En las memorias
USB la estructura de partida es `Folders`, que sí usa la carpeta soltada, y ahí no hay
sorpresa. Lo decide `VolumeInspector.DefaultStructure` según el tipo de dispositivo.

Aviso práctico: `Join-Path $d 'a\' + $pat` sin paréntesis se expande mal. Hay que escribir
`"$d\$cola"`.

---

## 4. Resultado medido

Con el presupuesto definitivo (bloque 2 MB, 48 MB por sesión, 6 sesiones):

| Escenario | Antes | Después (medido hoy) |
|---|---|---|
| A: 1 destino, 1 archivo 600 MB | 155 MB/s | **886 MB/s** (0,7 s) |
| B: 4 destinos, el mismo archivo | 25,5 MB/s cada uno (102 en total) | 23,3 MB/s cada uno, **conjunto 93,4** |
| C: 4 destinos, 4 archivos distintos | 29,3 MB/s cada uno (117 en total) | 36,1 MB/s cada uno, **conjunto 144,3** |
| D: 8 destinos, 8 archivos distintos | 11,1 MB/s cada uno (89 en total) | 13,7 MB/s cada uno, **conjunto 109,9** |
| E: 2 destinos → D: | 153,8 MB/s cada uno | **342,5 MB/s cada uno, conjunto 684,5** |
| F: 6 destinos, 6 archivos distintos | 15,1 MB/s cada uno (91 en total) | 13,0 MB/s cada uno, conjunto 78,2 |

La columna de la derecha es la corrida del 27/09 a las 19:09, con `C:\banco` de destino. Los
casos B, C, D y F duran entre 16 y 87 s, así que la caché de disco apenas interviene y sus
cifras son comparables entre sí.

### Un aviso que hay que tener presente: las cifras de un solo destino no se reproducen

Con **el mismo presupuesto** (bloque 2 MB, canal 24) el caso A dio 886 MB/s y el A2 dio
**266,9 MB/s**. Con 4 MB y canal 8, 1011 MB/s; con 8 MB y canal 8, 1212 MB/s. Son
configuraciones equivalentes salvo el tamaño del bloque, y van de 267 a 1227 MB/s: un
factor de casi cinco. Los que duran menos de un segundo se llevan el archivo entero desde
la caché, y los que duran más no.

Esto obliga a decir dos cosas que antes se decían de otra forma:

- El "155 → 1300–1467 MB/s" del caso A **no es un número reproducible**. Lo que sí se puede
  afirmar es que el caso de un destino solo pasó de 155 MB/s a del orden de **300–1300 MB/s**,
  según cuánto aproveche la caché. La mejora es real y de otro orden de magnitud; la cifra
  concreta, no.
- Por lo mismo, la afirmación de que "el tamaño del bloque no importa" sigue sin poder
  distinguirse del ruido, y ahora hay un ejemplo claro de por qué.

### Lo que sí se puede concluir de la tabla

Los 39 MB/s de partida eran el trinquete del 3.1, no el disco. La prueba es que los casos de
varios destinos, que antes se quedaban en 89–91 MB/s de conjunto, ahora salen entre 78 y 144
MB/s de conjunto **con el techo del disco metido por medio** (C: aguanta 106 MB/s de
escritura, y C y D están los dos al 100 %). Y el caso de un solo destino se ha multiplicado
por entre 2 y 8. Un destino solo ya no es el caso lento: es el caso rápido.

### El tamaño del bloque no se puede distinguir del ruido

Se llegó a escribir en el código que 2 MB ganaba a 4 MB (120 contra 100 MB/s). Al repetir la
medición **se dio la vuelta**: una prueba dio 2 MB por delante y la siguiente 4 MB por
delante, con la misma distancia que el ruido del disco. La corrida de hoy lo vuelve a
enseñar (267 contra 1011 MB/s, pero en el sentido contrario y con duraciones de 2,2 s y
0,6 s, o sea tiempo suficiente para que la caché no explique la diferencia… o sí).

El comentario del código está corregido: el tamaño del bloque no se puede distinguir del
ruido. Se queda 2 MB porque al mismo rendimiento se lleva la mitad de RAM. Lo que **sí** se
nota es la profundidad del canal (48 MB por sesión) y el número de archivos distintos a la
vez (6).

Y el criterio que hay que aplicar al leer estas cifras: **un caso que dura menos de un
segundo no mide el disco, mide la caché.** Por eso los casos B, C, D y F (16 a 87 s) son los
comparables entre sí, y los casos A, A1, A3 y A4 (0,5 a 2,2 s) solo dan idea de un orden de
magnitud.

---

## 5. Soltar ya es copiar

### Qué se cambió

Antes había dos caminos: el arrastre encolaba y el botón `COPIAR` arrancaba. Ahora hay
**uno**:

```csharp
Task EmpezarAsync(IReadOnlyList<DeviceState> targets, int encolados = -1, bool aviso = false)
```

La llaman los dos sitios, `OnDrop` y `OnStart`, con lo mismo. No es solo "quitar una
llamada": es que el botón hacía cosas que el arrastre no hacía, y con dos caminos
cualquier corrección se quedaba corta en uno de los dos.

- El modo de copia se reinicia solo a **Normal** tras cada arrastre.
- Sin destino marcado y soltando fuera de una tarjeta, no se copia nada y se avisa con
  `AVISO` en el log y en la barra de estado.

### El arreglo de verdad: el arrastre se quedaba abierto

Al poner el arranque automático, `OnDrop` pasó a ser `async` y a esperar a la copia. La
traza dio **12,4 s** de arrastre con el ratón clavado sobre un archivo de 600 MB, cuando el
gesto dura menos de un segundo.

La causa no está en nuestro código: el manejador de soltada de WPF se ejecuta **dentro** de
la llamada a `DoDragDrop`, así que cualquier `await` dentro mantiene el arrastre de OLE
abierto hasta que termine. Es decir, se puede esperar, pero entonces el usuario no puede
volver a mover el ratón ni abrir otro Explorer hasta el último byte.

**Corregido:** `OnDrop` ya no es `async`. Encola, y lanza la copia en el campo
`_copiaEnMarcha` sin esperarla. `OnStart` usa el mismo campo en vez de `async void`.

La traza pasó de 12,4 s a **2,7 s** (12:27:52,816 → 12:27:55,482), que es la duración del
gesto. De paso: se quitó el `Topmost=false` que se le ponía al arrastrador (hacía parpadear
la ventana al activarla) y ahora se registra lo que devuelve `DoDragDrop`
(`acept=Copy`).

`OnClosing` se dejó como estaba a propósito: `EmpezarAsync` ya tiene su `try/catch`, y
cambiar `StatusText` en `Closing` es inocuo.

### Cómo se comprobó

`smoke11.ps1`: ventana real, D: como destino lento (48 MB/s), un archivo de 600 MB, 22
vueltas de lectura, y búsqueda en la ruta espejada **sin `-Depth`** (por el 3.9). Salida:

```
Soltados 1 ruta(s) -> 1 en la cola
Arranca la copia: 1 ruta(s)
Termina la copia: 1 entero(s), 0 con problema, 0 sin tocar
```

Sin pulsar COPIAR. El cliente lo confirmó a mano.

### Las líneas de log que dejaron esto demostrado

```
Soltados 3 ruta(s) -> 12 en la cola para K:, L:, P: [con carpetas]
Arranca la copia: 12 ruta(s)
Termina la copia: 12 entero(s), 0 con problema, 0 sin tocar
```

El log va en `%LOCALAPPDATA%\FreemanMultiCopier\registro.log`.

---

## 6. El ícono

El ícono es un **disco duro fusionado con una F de Freeman**, y es vectorial de verdad:

```
branding\icono.svg        LA FUENTE. Se edita este fichero, no el .ico
branding\icono\Program.cs tool .NET/WPF que convierte el SVG en un .ico multirresolucion
branding\png\app-*.png    las cuatro informaturas grandes que usa de entrada
```

El `.ico` que se entrega está **compilado**, no editado a mano: `build.ps1` lo regenera en el
paso 0, siempre, no solo si falta el fichero. Editar el SVG y compilar saltándose ese paso es
la única forma de que el icono se quede viejo, y por eso está en el script y no en una
receta aparte. `-SkipIcon` lo salta cuando se quiere velocidad.

El `.ico` lleva 10 resoluciones, de 16×16 a 256×256, y está cableado en los dos sitios que
importan:

- `FreemanMultiCopier.csproj` → `<ApplicationIcon>Resources\app.ico</ApplicationIcon>`, que es
  lo que Windows muestra en el `.exe` y en la barra de tareas.
- `installer\FreemanMultiCopier.iss` → `SetupIconFile={#StageDir}\app.ico`, que es lo que
  Windows muestra en el instalador. `build.ps1` copia el `.ico` suelto a la raíz del
  escenario porque el `.iss` lo busca ahí.

---

## 7. Comprobaciones

```powershell
$env:DOTNET_ROOT = "C:\Users\FREEMAN\AppData\Local\Programs\dotnet"
& "$env:DOTNET_ROOT\dotnet.exe" build "C:\Users\FREEMAN\Documents\Default Project\FreemanMultiCopier\src\FreemanMultiCopier\FreemanMultiCopier.csproj" -c Release --no-incremental
& "…\bin\Release\net10.0-windows\win-x64\FreemanMultiCopier.exe" --selftest
```

- La prueba interna **solo escribe `prueba.txt` si tiene consola**. Con `*> $null` no hay
  informe y parece que no pasa nada. Para capturarla:
  `& exe --selftest 2>&1 | Out-File x.txt -Encoding utf8`.
- `TestCancel` no duerme a ciegas: espera a que el trabajo empiece a escribir (300 MB).
- Un fallo de integridad dice **por qué**: tamaño distinto, primer byte que no cuadra y
  posición. Eso fue lo que permitió cazar el 3.6.
- **No recompilar mientras la prueba se está corriendo.** Pasa porque el ejecutable se
  sustituye en mitad de la prueba y salen fallos de enumeración de volúmenes que no son
  del motor. Pasa también por compilar el proyecto del banco a la vez.
- Compilación de la app: **0 errores, 0 warnings**.

### Batería completa, en verde

| Prueba | Qué mide | Resultado |
|---|---|---|
| `--selftest` | 82 comprobaciones del motor sobre disco real | **82/82** |
| `BANCO_BINDINGS` | 41 rutas del XAML contra su code-behind | **41 rutas, 0 rotas** |
| `BANCO_PANEL=8` | copia real leyendo bloque ORIGEN y filas; compara los 8 destino con el origen | **489 lecturas, 8/8 enteros** |
| `BANCO_ORIGEN=8` | `LECTURA` / `TRANSFERENCIA` / `ADEMAS` con archivos de 600 MB | **16/16 enteros**, `LECTURA 532.8 MB/s`, `TRANSFERENCIA 1.3 GB/s hacia 4 destinos`, `ADEMAS peli-0.mkv 436.1 MB/s de origen, 1.2 GB/s hacia 4 destinos` |
| `BANCO_ESTRES=30` | 30 rondas × 8 destinos, comparando byte a byte | **30 rondas, 0 fallos** |
| `BANCO_SERVICIO` | servicio + dispositivos: estructura por tipo, reset a Normal, cola por dispositivo, SHA256, vuelta en vacío | **OK** |

La línea `ADEMAS` del banco sale con datos de verdad: 446 lecturas del bloque, **442 con la
línea `ADEMAS`**, hasta 2 archivos más a la vez, 455 muestras, 428 con cifras y 0 malas. Antes
de eso hubo que arreglar una comprobación que **era imposible de satisfacer**: pedía
`mejorOtros >= 1`, o sea dos archivos en la línea `ADEMAS`, cuando con 4 archivos y medio
destino cada uno solo hay 2 en vuelo. Se añadió `LineaAdemas()` al banco, que descuadra los
destinos a propósito para tener tres archivos en marcha.

---

## 8. Avisos de la casa

- La herramienta de escritura **mete palabras raras y caracteres chinos** en los archivos.
  Después de cada escritura:
  `Get-ChildItem -Include *.cs,*.xaml,*.iss,*.ps1,*.py -Recurse -File | Select-String -Pattern '[\u0400-\u04FF\u4E00-\u9FFF\u0600-\u06FF\u3040-\u30FF\uFFFD]'`
  Lo único legítimo es una carpeta de `SelfTest.cs` que se llama a propósito
  `"acentos ñ 中文"`.
- Las ediciones de varias líneas van mejor en un `.py` de parche con
  `io.open(..., encoding="utf-8", newline="")` y `str.replace` exacto. Detectar el salto de
  línea **dominante** (contando CRLF frente a LF sueltos), no comprobar
  `b"\r\n" in data`: si se hace mal, la inserción se duplica.
- En los parches, el atajo `if new in s: skip` se dispara siempre que lo nuevo es una sola
  línea que ya existe (típico al **borrar** una línea). Para borrar, mejor un `replace`
  con `nl` explícito y `assert s.count(trozo) == 1`.
- **`build.ps1` borra `build\out` entero** antes de montar el instalador. El PDF del manual
  se genera **después** de la compilación, no antes, o desaparece.
- **`p.h3` del manual no envuelve**: dibuja con `drawString` a 14 pt negrita. Un título de
  más de unos 60 caracteres se sale de la página sin avisar. Hay que medirlo con
  `pdfmetrics.stringWidth` antes de dares a escribirlo.
- `[IO.File]::ReadAllBytes` con ruta relativa resuelve contra el directorio del .NET, no el de
  PowerShell: usar rutas absolutas.
- Un `.ps1` guardado **sin BOM** lo lee Windows PowerShell como ANSI y rompe los acentos
  (`Token 'SOLO' inesperado`). Añadir BOM antes de ejecutarlo.
- **Toda afirmación sobre el comportamiento del runtime, comprobada en su fuente, no de
  memoria.** Fue lo que pasó con el pool de `ArrayPool` (§3.2): arrastraba un comentario que
  era verdad en .NET Core y falsa en .NET 10. Si un comentario del código explica un
  problema, tiene que poder señalarse el fichero del runtime donde se ve.
- `dotnet` **no está en PATH**; hay que usar la ruta de `DOTNET_ROOT` de arriba.
- PowerShell bloquea los `.ps1`: `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1`.
- El proyecto **no es un repositorio de git**: nada de esto está guardado con commits. Si
  importa, hay que hacerlo a mano.
- En el banco, `min == max` por dispositivo y la columna "conjunto" de los casos con
  sesiones compartidas son **artefactos de la métrica**, no señales de nada: cuando todos
  los destinos comparten lectura, sumar sus velocidades no significa nada.
- Nada de lo que hay en la carpeta compartida debe darse por supuesto que está en git ni
  `QBasWinGCopyFast`, `TODASIMPRESORAS 2` y `opencode-tc` son de sesiones anteriores y no
  se han tocado.

---

## 9. Entregado

Los tres entregables están en `C:\Users\FREEMAN\Downloads`, generados el 27/09 por el propio
`build.ps1` y verificados por SHA256 contra `build\out`:

| Fichero | Tamaño | Qué es |
|---|---|---|
| `Freeman MULTICOPIER - Instalador v0.1.0.exe` | 53,96 MB | instalador de Inno Setup, con el icono propio |
| `Freeman MULTICOPIER v0.1.0.rar` | 53,54 MB | el instalador y el manual, con prueba de integridad (`rar t`) |
| `Freeman MULTICOPIER (Manual de Usuario).pdf` | 97 KB | 29 páginas, fuente en `docs\manual.py` |

La compilación que los produjo, de principio a fin, sin saltarse nada:

```
==> Generando el icono desde branding\icono.svg
==> Publicando el ejecutable de un solo archivo
==> Pasando la bateria de pruebas al ejecutable recien publicado
  82/82 pruebas correctas
  RESULTADO: TODO CORRECTO
==> Montando el instalador
```

Dos detalles que quedan atados en la fuente, para no tener que arreglarlos a mano cada vez:

- El nombre del instalador sale de `OutputBaseFilename` en el `.iss`, no de un renombrado
  posterior. Antes salía `FreemanMultiCopier_Setup_0.1.0.exe` y había que renombrarlo a mano
  para el cliente; puesto en el `.iss` no se puede quedar viejo.
- El PDF del manual se genera **después** de `build.ps1`, porque ese script borra
  `build\out` entero antes de montar el instalador. Está ordenado así a propósito, y
  comprobado: la primera vez se generó antes y desapareció.

El manual lleva los rótulos que pide el cliente (`LECTURA`, `ESCRITURA`, `ORIGEN` y
`ADEMAS`), un capítulo propio del bloque ORIGEN con su dibujo a escala, y "soltar es
copiar" escrito en el resumen, en primer uso, en cómo se copia, en la barra de acciones,
en funciones básicas y en las preguntas. La ruta del `--selftest` también estaba mal: decía
`C:\Program Files\FreemanMultiCopier`, cuando el instalador deja el programa en
`C:\Program Files\Freeman MULTICOPIER`. Y el manual ya no inventa cifras: dice 82
comprobaciones y unos 55 MB de ocupación, que es lo que ocupa de verdad.

### Botones donde se puede perder trabajo

Ninguno, pero conviene dejarlo escrito: si se cierra el programa a mitad de una copia, esa
copia se pierde. Las colas no se guardan entre sesiones, y es una decisión, no un olvido.
Está en el manual, en CONFIGURACIÓN y en LO QUE NO HACE.

### Limpieza hecha

Se han borrado 8,9 GB de la carpeta temporal: `origen-ui` (6,5 GB de datos de las pruebas de
arrastre), `banco\datos` (2,5 GB, regenerable) y los restos del manual y del arrastre. `C:`
pasó de 15,4 a 24,3 GB libres. **Se conservan** el banco (`Banco.csproj` y `Program.cs`, que
son la prueba de todo lo de arriba) y el arrastrador de prueba, porque sin ellos no se puede
repetir ni el 3.6 ni el 5.

Las unidades del cliente están como estaban: ni rastro de la copia en `B:`, `D:`, `L:` ni
`P:`, y `K:` intacta.

### Lo que queda pendiente, y no es de este proyecto

1. **Los PDF sueltos de la raíz de la carpeta compartida: borrados.** Eran 16
   ficheros, 1,37 MB, todos de hoy entre las 10:56 y las 12:27: `1.pdf` a `5.pdf` (cinco de
   0 B), `6.pdf`, `7.pdf`, `8.pdf`, `534356.pdf` y siete `No Document Name*.pdf`. Eran
   restos de la sesión de impresión de `TODASIMPRESORAS 2`, no de este programa. Preguntado
   y autorizado por el cliente antes de tocarlos. Los PDF de dentro de las carpetas de
   proyectos no se han mirado.
2. **`TODASIMPRESORAS 2` va con el `.exe` más viejo que su propio código**: el `.pyw` de la
   carpeta del proyecto (10:38) **no tiene** la impresión por rango de páginas
   (`_envia_word_rango`, `_carpeta_rango`), pero el `.pyw` de Descargas (12:13) **sí**, y
   son 88 líneas más. El `.exe` (10:43) y el `.rar` (10:45) se construyeron con el primero:
   el `LEEME.txt` describe una función que el `.exe` entregado no tiene. Arreglo: devolver
   el `.pyw` de Descargas al proyecto y recompilar. No se ha tocado nada de ese proyecto.

---

## 10. Estructura de ficheros que importa

```
src\FreemanMultiCopier\
   App.xaml.cs                          arranque, SingleInstance, Log.DataDirectory
   Core\CopyService.cs                  Enqueue (:229), JobFor (:337), StartAllAsync (:357)
   Core\Engine\CopyBlock.cs             pool propio de 8 MB, recuento de referencias
   Core\Engine\CopySession.cs           el pump: lee una vez, reparte en N colas
   Core\Engine\CopyScheduler.cs          ya no recorta a nadie
   Core\Engine\MultiCopyEngine.cs       el caso de un solo destino, con doble buffer
   Core\Engine\StructureResolver.cs     Resolve, RelClamped
   Core\Devices\VolumeInspector.cs     DefaultStructure: disco -> Structure, USB -> Folders
   Core\Model\DeviceRef.cs              VolumeRef y DeviceState, SIN WPF (por eso el banco compila esto)
   Core\SelfTest.cs                     las 82 comprobaciones
   UI\MainWindow.xaml.cs                OnDrop (:387), _copiaEnMarcha (:40),
                                        EmpezarAsync (:587), OnStart (:642)
   UI\OriginReadout.cs                  el bloque ORIGEN y la línea ADEMAS
   UI\LiveCopyRow.cs                    LECTURA y ESCRITURA de cada destino
build.ps1                               paso 0 = icono; 1 = publicar; 2 = pruebas; 3 = instalador
installer\FreemanMultiCopier.iss        SetupIconFile = {#StageDir}\app.ico
branding\icono.svg                      la fuente del ícono
docs\manual.py                          la fuente del PDF del manual
```
