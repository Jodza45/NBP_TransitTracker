import { useEffect, useMemo, useRef, useState } from 'react'
import { MapContainer, Marker, Popup, TileLayer, useMap, useMapEvents } from 'react-leaflet'
import { Icon, Marker as LeafletMarker } from 'leaflet'
import type { LeafletEvent, LeafletMouseEvent } from 'leaflet'
import './App.css'

type LatLng = {
  lat: number
  lng: number
}

type Station = {
  id: string
  name: string
  latitude: number
  longitude: number
}

type NearbyStation = Station & {
  distanceMeters: number
}

type StationArrival = {
  vehicleId: string
  lineId: string
  estimatedSeconds: number
}

type StationArrivalsResponse = {
  stationId: string
  stationName: string
  arrivals: StationArrival[]
}

type LineDetails = {
  id: string
  name: string
  stationIds: string[]
}

type VehicleLocation = {
  vehicleId: string
  latitude: number
  longitude: number
  timestamp: string
}

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5000'
const nisCenter: LatLng = { lat: 43.3209, lng: 21.8958 }
const nisBounds: [[number, number], [number, number]] = [
  [43.255, 21.78],
  [43.39, 22.02],
]
const minStationsRadiusMeters = 500
const maxStationsRadiusMeters = 5000
const cyrToLatMap: Record<string, string> = {
  '\u0410': 'A', '\u0430': 'a', '\u0411': 'B', '\u0431': 'b', '\u0412': 'V', '\u0432': 'v', '\u0413': 'G', '\u0433': 'g', '\u0414': 'D', '\u0434': 'd',
  '\u0402': 'Dj', '\u0452': 'dj', '\u0415': 'E', '\u0435': 'e', '\u0416': 'Z', '\u0436': 'z', '\u0417': 'Z', '\u0437': 'z', '\u0418': 'I', '\u0438': 'i',
  '\u0408': 'J', '\u0458': 'j', '\u041A': 'K', '\u043A': 'k', '\u041B': 'L', '\u043B': 'l', '\u0409': 'Lj', '\u0459': 'lj', '\u041C': 'M', '\u043C': 'm',
  '\u041D': 'N', '\u043D': 'n', '\u040A': 'Nj', '\u045A': 'nj', '\u041E': 'O', '\u043E': 'o', '\u041F': 'P', '\u043F': 'p', '\u0420': 'R', '\u0440': 'r',
  '\u0421': 'S', '\u0441': 's', '\u0422': 'T', '\u0442': 't', '\u040B': 'C', '\u045B': 'c', '\u0423': 'U', '\u0443': 'u', '\u0424': 'F', '\u0444': 'f',
  '\u0425': 'H', '\u0445': 'h', '\u0426': 'C', '\u0446': 'c', '\u0427': 'C', '\u0447': 'c', '\u040F': 'Dz', '\u045F': 'dz', '\u0428': 'S', '\u0448': 's',
}

function toLatin(text: string): string {
  return Array.from(text).map((ch) => cyrToLatMap[ch] ?? ch).join('')
}

function svgDataUri(svg: string): string {
  return `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`
}

const stationIcon = new Icon({
  iconUrl: svgDataUri(`<svg xmlns='http://www.w3.org/2000/svg' width='36' height='36' viewBox='0 0 34 34'>
  <circle cx='17' cy='17' r='16' fill='white' stroke='#356ea8' stroke-width='2'/>
  <circle cx='17' cy='17' r='11' fill='#4f89c4'/>
  <rect x='10' y='12' width='14' height='10' rx='2' fill='white'/>
  <circle cx='13' cy='23' r='1.6' fill='white'/>
  <circle cx='21' cy='23' r='1.6' fill='white'/>
  </svg>`),
  iconSize: [36, 36],
  iconAnchor: [18, 18],
  popupAnchor: [0, -16],
})

const selectedIcon = new Icon({
  iconUrl: 'https://raw.githubusercontent.com/pointhi/leaflet-color-markers/master/img/marker-icon-red.png',
  shadowUrl: 'https://cdn.jsdelivr.net/npm/leaflet@1.9.4/dist/images/marker-shadow.png',
  iconSize: [25, 41],
  iconAnchor: [12, 41],
})

const trackedVehicleIcon = new Icon({
  iconUrl: svgDataUri(`<svg xmlns='http://www.w3.org/2000/svg' width='30' height='30' viewBox='0 0 30 30'>
  <circle cx='15' cy='15' r='13' fill='#f59e0b' stroke='#7c2d12' stroke-width='2'/>
  <rect x='8' y='9' width='14' height='9' rx='2' fill='white'/>
  <circle cx='11' cy='20' r='1.8' fill='#111827'/>
  <circle cx='19' cy='20' r='1.8' fill='#111827'/>
  </svg>`),
  iconSize: [30, 30],
  iconAnchor: [15, 15],
  popupAnchor: [0, -14],
})

function clampToNis(position: LatLng): LatLng {
  const [[minLat, minLng], [maxLat, maxLng]] = nisBounds
  return {
    lat: Math.max(minLat, Math.min(maxLat, position.lat)),
    lng: Math.max(minLng, Math.min(maxLng, position.lng)),
  }
}

function toRadians(value: number): number {
  return (value * Math.PI) / 180
}

function distanceMeters(from: LatLng, to: LatLng): number {
  const earthRadius = 6371000
  const dLat = toRadians(to.lat - from.lat)
  const dLng = toRadians(to.lng - from.lng)
  const a =
    Math.sin(dLat / 2) * Math.sin(dLat / 2) +
    Math.cos(toRadians(from.lat)) * Math.cos(toRadians(to.lat)) * Math.sin(dLng / 2) * Math.sin(dLng / 2)

  return earthRadius * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a))
}

function RecenterOnSelection({ position }: { position: LatLng }) {
  const map = useMap()

  useEffect(() => {
    map.setView([position.lat, position.lng])
  }, [position, map])

  return null
}

function ClickToPlaceMarker({ onPick }: { onPick: (position: LatLng) => void }) {
  useMapEvents({
    click: (event: LeafletMouseEvent) => {
      onPick(clampToNis({ lat: event.latlng.lat, lng: event.latlng.lng }))
    },
  })

  return null
}

function App() {
  const [selectedPosition, setSelectedPosition] = useState<LatLng>(nisCenter)
  const [allStations, setAllStations] = useState<Station[]>([])
  const [selectedStation, setSelectedStation] = useState<NearbyStation | null>(null)
  const [stationArrivals, setStationArrivals] = useState<StationArrivalsResponse | null>(null)
  const [lineDetailsById, setLineDetailsById] = useState<Record<string, LineDetails>>({})
  const [trackedVehicleId, setTrackedVehicleId] = useState<string | null>(null)
  const [trackedVehiclePosition, setTrackedVehiclePosition] = useState<LatLng | null>(null)
  const [isLoadingStations, setIsLoadingStations] = useState(false)
  const [isLoadingArrivals, setIsLoadingArrivals] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [searchTerm, setSearchTerm] = useState('')
  const [isStationsReady, setIsStationsReady] = useState(false)
  const [stationsRadiusMeters, setStationsRadiusMeters] = useState(minStationsRadiusMeters)
  const hasInitialized = useRef(false)
  const hasAutoLocated = useRef(false)

  const nearbyStations = useMemo(() => {
    return allStations
      .map((station) => ({
        ...station,
        distanceMeters: distanceMeters(selectedPosition, { lat: station.latitude, lng: station.longitude }),
      }))
      .filter((station) => station.distanceMeters <= stationsRadiusMeters)
      .sort((a, b) => a.distanceMeters - b.distanceMeters)
  }, [allStations, selectedPosition, stationsRadiusMeters])

  const filteredStations = useMemo(() => {
    if (!searchTerm.trim()) {
      return nearbyStations
    }

    const normalizedSearch = toLatin(searchTerm).toLowerCase()
    return nearbyStations.filter((station) => toLatin(station.name).toLowerCase().includes(normalizedSearch))
  }, [nearbyStations, searchTerm])

  const stationNameById = useMemo(() => {
    return allStations.reduce<Record<string, string>>((acc, station) => {
      acc[station.id] = station.name
      return acc
    }, {})
  }, [allStations])

  useEffect(() => {
    if (hasInitialized.current) {
      return
    }

    hasInitialized.current = true

    const initializeStations = async () => {
      setIsLoadingStations(true)
      setError(null)

      try {
        await fetch(`${apiBaseUrl}/api/v1/stations/import-osm-nis`, { method: 'POST' }).catch(() => undefined)

        const allEndpoints = [`${apiBaseUrl}/api/v1/stations/all`, `${apiBaseUrl}/api/stations/all`]
        let stations: Station[] = []

        for (const endpoint of allEndpoints) {
          const response = await fetch(endpoint)
          if (!response.ok) {
            continue
          }

          const data = (await response.json()) as Station[]
          if (data.length > 0) {
            stations = data
            break
          }
        }

        if (stations.length === 0) {
          const nearbyResponse = await fetch(
            `${apiBaseUrl}/api/v1/stations/nearby?lat=${nisCenter.lat}&lng=${nisCenter.lng}&radius=12000`,
          )

          if (nearbyResponse.ok) {
            const nearby = (await nearbyResponse.json()) as NearbyStation[]
            stations = nearby.map((x) => ({
              id: x.id,
              name: x.name,
              latitude: x.latitude,
              longitude: x.longitude,
            }))
          }
        }

        if (stations.length === 0) {
          throw new Error('Failed to load stations.')
        }

        setAllStations(stations.map((station) => ({ ...station, name: toLatin(station.name) })))
      } catch {
        setError('Nis stations could not be loaded right now.')
      } finally {
        setIsLoadingStations(false)
        setIsStationsReady(true)
      }
    }

    void initializeStations()
  }, [])

  function locateMe() {
    if (!navigator.geolocation) {
      setError('Geolocation is not supported in this browser.')
      return
    }

    navigator.geolocation.getCurrentPosition(
      (position) => {
        const next = clampToNis({
          lat: position.coords.latitude,
          lng: position.coords.longitude,
        })
        setSelectedPosition(next)
        setError(null)
      },
      () => {
        setError('Location access denied or unavailable.')
      },
      { enableHighAccuracy: true, timeout: 10000, maximumAge: 10000 },
    )
  }

  useEffect(() => {
    if (hasAutoLocated.current) {
      return
    }

    hasAutoLocated.current = true
    locateMe()
  }, [])

  useEffect(() => {
    const loadLineDetails = async () => {
      if (!stationArrivals || stationArrivals.arrivals.length === 0) {
        return
      }

      const lineIds = Array.from(new Set(stationArrivals.arrivals.map((x) => x.lineId)))
      const missing = lineIds.filter((lineId) => !lineDetailsById[lineId])
      if (missing.length === 0) {
        return
      }

      const loaded = await Promise.all(
        missing.map(async (lineId) => {
          const urls = [`${apiBaseUrl}/api/v1/lines/${lineId}`, `${apiBaseUrl}/api/lines/${lineId}`]

          for (const url of urls) {
            const response = await fetch(url)
            if (!response.ok) {
              continue
            }

            return (await response.json()) as LineDetails
          }

          return null
        }),
      )

      const next: Record<string, LineDetails> = {}
      for (const line of loaded) {
        if (line) {
          next[line.id] = line
        }
      }

      if (Object.keys(next).length > 0) {
        setLineDetailsById((prev) => ({ ...prev, ...next }))
      }
    }

    void loadLineDetails()
  }, [lineDetailsById, stationArrivals])

  function getLineBadge(lineId: string): string {
    const line = lineDetailsById[lineId]
    if (!line) {
      return lineId.replace(/^gtfs:/i, '').split(':')[0] || lineId
    }

    const normalized = toLatin(line.name)
    const first = normalized.split('-')[0]?.trim()
    return first || lineId
  }

  function getLineDirection(lineId: string): string {
    const line = lineDetailsById[lineId]
    if (!line || line.stationIds.length === 0) {
      return 'Direction unavailable'
    }

    const firstName = stationNameById[line.stationIds[0]] ?? line.stationIds[0]
    const lastName = stationNameById[line.stationIds[line.stationIds.length - 1]] ?? line.stationIds[line.stationIds.length - 1]
    return `${firstName} - ${lastName}`
  }

  async function getVehicleLocation(vehicleId: string): Promise<VehicleLocation | null> {
    const urls = [`${apiBaseUrl}/api/v1/vehicles/${vehicleId}/location`, `${apiBaseUrl}/api/vehicles/${vehicleId}/location`]

    for (const url of urls) {
      const response = await fetch(url)
      if (!response.ok) {
        continue
      }

      return (await response.json()) as VehicleLocation
    }

    return null
  }

  const trackVehicle = async (arrival: StationArrival) => {
    if (trackedVehicleId === arrival.vehicleId) {
      setTrackedVehicleId(null)
      setTrackedVehiclePosition(null)
      setError(null)
      return
    }

    setTrackedVehicleId(arrival.vehicleId)

    const location = await getVehicleLocation(arrival.vehicleId)
    if (!location) {
      setError('Vehicle location is not available yet.')
      return
    }

    const next = clampToNis({ lat: location.latitude, lng: location.longitude })
    setTrackedVehiclePosition(next)
    setSelectedPosition(next)
    setError(null)
  }

  useEffect(() => {
    if (!trackedVehicleId) {
      return
    }

    const refresh = async () => {
      const location = await getVehicleLocation(trackedVehicleId)
      if (!location) {
        return
      }

      const next = clampToNis({ lat: location.latitude, lng: location.longitude })
      setTrackedVehiclePosition(next)
    }

    void refresh()
    const timer = window.setInterval(() => {
      void refresh()
    }, 2500)

    return () => {
      window.clearInterval(timer)
    }
  }, [trackedVehicleId])

  const selectStation = async (station: NearbyStation) => {
    setSelectedStation(station)
    setIsLoadingArrivals(true)
    setError(null)

    try {
      const response = await fetch(`${apiBaseUrl}/api/v1/stations/${station.id}/arrivals`)
      if (!response.ok) {
        throw new Error('Failed to load arrivals.')
      }

      const data = (await response.json()) as StationArrivalsResponse
      setStationArrivals(data)
    } catch {
      setError('Could not load arrivals for this station yet.')
      setStationArrivals(null)
    } finally {
      setIsLoadingArrivals(false)
    }
  }

  return (
    <div className="layout">
      <header className="topbar">
        <input
          className="search"
          type="text"
          placeholder="Search Nis stations"
          value={searchTerm}
          onChange={(event) => setSearchTerm(event.target.value)}
        />
      </header>

      <main className="map-wrap">
        <MapContainer
          center={[selectedPosition.lat, selectedPosition.lng]}
          zoom={15}
          minZoom={12}
          maxZoom={18}
          maxBounds={nisBounds}
          maxBoundsViscosity={1}
          scrollWheelZoom
          className="map"
        >
          <TileLayer
            attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
            url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
          />

          <RecenterOnSelection position={selectedPosition} />
          <ClickToPlaceMarker onPick={setSelectedPosition} />

          <Marker
            position={[selectedPosition.lat, selectedPosition.lng]}
            icon={selectedIcon}
            draggable
            eventHandlers={{
              dragend: (event: LeafletEvent) => {
                const marker = event.target as LeafletMarker
                const latLng = marker.getLatLng()
                setSelectedPosition(clampToNis({ lat: latLng.lat, lng: latLng.lng }))
              },
            }}
          >
            <Popup>Your selected Nis position</Popup>
          </Marker>

          {filteredStations.map((station) => (
            <Marker
              key={station.id}
              position={[station.latitude, station.longitude]}
              icon={stationIcon}
              eventHandlers={{ click: () => void selectStation(station) }}
            >
              <Popup>
                <strong>{station.name}</strong>
                <br />
                {Math.round(station.distanceMeters)} m away
              </Popup>
            </Marker>
          ))}

          {trackedVehicleId && trackedVehiclePosition ? (
            <Marker position={[trackedVehiclePosition.lat, trackedVehiclePosition.lng]} icon={trackedVehicleIcon}>
              <Popup>Tracking vehicle {trackedVehicleId}</Popup>
            </Marker>
          ) : null}
        </MapContainer>

        <button className="locate-fab" onClick={locateMe} aria-label="Use my location" title="Use my location">
          <svg viewBox="0 0 24 24" role="img" aria-hidden="true">
            <path d="M21.2 2.8a1 1 0 0 0-1.05-.24L3.55 9.02a1 1 0 0 0 .1 1.9l7.12 2.06 2.06 7.12a1 1 0 0 0 1.9.1l6.46-16.6a1 1 0 0 0-.24-1.05ZM13.34 17.2l-1.5-5.2a1 1 0 0 0-.68-.68l-5.2-1.5 11.55-4.5-4.17 11.88Z" />
          </svg>
        </button>
      </main>

      <aside className="bottom-sheet">
        {error ? <p className="error">{error}</p> : null}

        {!selectedStation ? (
          <section>
            <div className="range-row">
              <h2>Nearby Stations ({filteredStations.length})</h2>
              <div className="range-control">
                <label htmlFor="rangeMeters">
                  Range: {stationsRadiusMeters >= 1000 ? `${(stationsRadiusMeters / 1000).toFixed(1)} km` : `${stationsRadiusMeters} m`}
                </label>
                <input
                  id="rangeMeters"
                  type="range"
                  min={minStationsRadiusMeters}
                  max={maxStationsRadiusMeters}
                  step={100}
                  value={stationsRadiusMeters}
                  onChange={(event) => setStationsRadiusMeters(Number(event.target.value))}
                />
              </div>
            </div>

            {!isStationsReady || isLoadingStations ? <p>Loading stations...</p> : null}
            <ul className="station-list">
              {filteredStations.map((station) => (
                <li key={station.id}>
                  <button type="button" className="station-item" onClick={() => void selectStation(station)}>
                    <span>{station.name}</span>
                    <small>{Math.round(station.distanceMeters)} m</small>
                  </button>
                </li>
              ))}
            </ul>
          </section>
        ) : (
          <section>
            <div className="station-title-row">
              <h2>{selectedStation.name}</h2>
              <button type="button" className="ghost" onClick={() => setSelectedStation(null)}>
                Back
              </button>
            </div>

            {isLoadingArrivals ? <p>Loading arrivals...</p> : null}

            <ul className="arrival-list">
              {stationArrivals?.arrivals.map((arrival) => (
                <li key={`${arrival.vehicleId}-${arrival.lineId}`}>
                  <button
                    type="button"
                    className={`arrival-track-btn ${trackedVehicleId === arrival.vehicleId ? 'is-tracked' : ''}`}
                    onClick={() => void trackVehicle(arrival)}
                    title="Show and track this bus on map"
                  >
                    <div className="arrival-row">
                      <span className="line-chip">{getLineBadge(arrival.lineId)}</span>
                      <span>{getLineDirection(arrival.lineId)}</span>
                      <strong>{Math.max(0, Math.round(arrival.estimatedSeconds / 60))} min</strong>
                    </div>
                  </button>
                </li>
              ))}
            </ul>

            {!isLoadingArrivals && (!stationArrivals || stationArrivals.arrivals.length === 0) ? (
              <p>No live arrivals yet for this station.</p>
            ) : null}
          </section>
        )}
      </aside>
    </div>
  )
}

export default App





