import { DefaultHttpClient, HubConnectionBuilder, LogLevel, NullLogger } from '@microsoft/signalr'

export function openNotificationsConnection(token, onUnauthorized) {
  class SessionHttpClient extends DefaultHttpClient {
    async send(request) {
      try {
        return await super.send(request)
      } catch (error) {
        // SignalR wraps negotiate failures and discards the status code.
        if (error.statusCode === 401) onUnauthorized()
        throw error
      }
    }
  }

  return new HubConnectionBuilder()
    .configureLogging(LogLevel.None)
    .withUrl('/hubs/notifications', {
      accessTokenFactory: () => token,
      httpClient: new SessionHttpClient(NullLogger.instance),
      timeout: 10000,
    })
    .withAutomaticReconnect()
    .build()
}

export function jwtExpiration(token) {
  try {
    const claims = JSON.parse(atob(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')))
    return Number.isFinite(claims.exp) ? claims.exp * 1000 : null
  } catch {
    return null
  }
}
