# Freeman MULTICOPIER

Copiador de archivos a varios destinos a la vez para Windows. Se sueltan unos
archivos sobre la ventana y se copian a todas las unidades conectadas, sin
tocar ningun boton.

- Copia a N unidades en paralelo, con un solo paso de lectura por bloque.
- 4 modos de copia y 8 estructuras de carpetas de destino.
- Panel izquierdo con el origen, el destino y el espacio libre de cada unidad.
- Barra de progreso por unidad, con la velocidad en vivo.
- Bloque ORIGEN: lectura y transferencia, con la tasa de cada archivo.
- El modo de copia se vuelve a Normal solo despues de cada arrastre.

No hay licencias, ni limites de dispositivos, ni cuentas. Todo el codigo es
original: el motor de copia esta escrito desde cero y no usa librerias de
terceros para copiar.

## Requisitos

- Windows 10 1809 o superior, x64.
- Para compilar: el SDK de .NET 10.
- Para generar el instalador: Inno Setup 6.

## Compilar

```powershell
.\build.ps1
```

El script hace cuatro pasos: regenera el icono desde `branding\icono.svg`,
publica el ejecutable, pasa las 82 pruebas internas y llama a Inno Setup.
Salen tres ficheros en `build\out`:

| Fichero | Que es |
| --- | --- |
| `Freeman MULTICOPIER - Instalador v0.1.0.exe` | instalador |
| `Freeman MULTICOPIER v0.1.0.rar` | instalador y manual juntos |
| `Freeman MULTICOPIER (Manual de Usuario).pdf` | manual, 29 paginas |

Opciones utiles:

| Opcion | Para que |
| --- | --- |
| `-SkipTests` | no perder tiempo con las 82 pruebas |
| `-SkipIcon` | no rehacer el `.ico` desde el SVG |
| `-NoInstaller` | parar antes de Inno Setup |

El manual en PDF lo genera `docs\manual.py` (necesita `reportlab`). Se hace
despues de `build.ps1`, porque ese script borra `build\out` entero.

## Pruebas

El ejecutable lleva las pruebas dentro, sin proyecto de test aparte:

```powershell
& .\src\FreemanMultiCopier\bin\Release\net10.0-windows\win-x64\publish\FreemanMultiCopier.exe --selftest
```

82 comprobaciones. La ultima linea tiene que decir `RESULTADO: TODO CORRECTO`.
Solo escribe `prueba.txt` si tiene consola delante.

## Como esta repartido

| Ruta | Que hay |
| --- | --- |
| `src\FreemanMultiCopier\Core\Engine` | el motor: bloques, sesiones, planificador |
| `src\FreemanMultiCopier\Core\Devices` | deteccion de unidades y sus cambios |
| `src\FreemanMultiCopier\Core\Model` | el trabajo de copia, los destinos, el origen |
| `src\FreemanMultiCopier\UI` | la ventana, el panel y el bloque ORIGEN |
| `branding` | el icono: `icono.svg` es la fuente de verdad |
| `installer` | el `.iss` de Inno Setup |
| `docs\manual.py` | el generador del manual en PDF |
| `build.ps1` | compilar, probar y empaquetar |

`ESTADO.md` cuenta con detalle como se llegó hasta aquí: los problemas que
aparecieron, lo que se midió, y las decisiones que se tomaron y por qué.
