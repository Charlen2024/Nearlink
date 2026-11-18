package main

import (
	"context"
	"embed"
	"encoding/json"
	"errors"
	"io/fs"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
	"time"
)

//go:embed web/*
var webFiles embed.FS

type healthResponse struct {
	Status string `json:"status"`
}

type siteMetadata struct {
	Name       string `json:"name"`
	Version    string `json:"version"`
	Repository string `json:"repository"`
	Releases   string `json:"releases"`
}

type releaseManifest struct {
	Platform       string `json:"platform"`
	Version        string `json:"version"`
	VersionCode    int    `json:"versionCode,omitempty"`
	MinimumVersion string `json:"minimumVersion,omitempty"`
	Notes          string `json:"notes,omitempty"`
	SHA256         string `json:"sha256"`
	DownloadURL    string `json:"downloadUrl"`
}

func main() {
	logger := slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))
	addr := env("NEARLINK_ADDR", "127.0.0.1:8080")
	releasesDir := env("NEARLINK_RELEASES_DIR", "releases")
	staticFiles, err := fs.Sub(webFiles, "web")
	if err != nil {
		logger.Error("load embedded site", "error", err)
		os.Exit(1)
	}

	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, _ *http.Request) {
		writeJSON(w, http.StatusOK, healthResponse{Status: "ok"})
	})
	mux.HandleFunc("GET /readyz", func(w http.ResponseWriter, _ *http.Request) {
		writeJSON(w, http.StatusOK, healthResponse{Status: "ready"})
	})
	mux.HandleFunc("GET /api/v1/site", func(w http.ResponseWriter, _ *http.Request) {
		writeJSON(w, http.StatusOK, siteMetadata{
			Name:       "Nearlink",
			Version:    env("NEARLINK_VERSION", "1.0.0"),
			Repository: "https://github.com/Charlen2024/Nearlink",
			Releases:   "https://github.com/Charlen2024/Nearlink/releases",
		})
	})
	mux.HandleFunc("GET /api/v1/releases/{platform}", func(w http.ResponseWriter, r *http.Request) {
		platform := r.PathValue("platform")
		if platform != "windows" && platform != "android" {
			http.NotFound(w, r)
			return
		}
		manifestPath := filepath.Join(releasesDir, platform+".json")
		file, err := os.Open(manifestPath)
		if err != nil {
			http.NotFound(w, r)
			return
		}
		defer file.Close()
		var manifest releaseManifest
		if err := json.NewDecoder(http.MaxBytesReader(w, file, 32*1024)).Decode(&manifest); err != nil || manifest.Platform != platform || strings.TrimSpace(manifest.Version) == "" {
			http.Error(w, "更新清单不可用。", http.StatusServiceUnavailable)
			return
		}
		manifest.DownloadURL = "/api/v1/releases/" + platform + "/download"
		writeJSON(w, http.StatusOK, manifest)
	})
	mux.HandleFunc("GET /api/v1/releases/{platform}/download", func(w http.ResponseWriter, r *http.Request) {
		platform := r.PathValue("platform")
		name, contentType := "", ""
		switch platform {
		case "windows":
			name, contentType = "Nearlink-Windows-x64.zip", "application/zip"
		case "android":
			name, contentType = "Nearlink-Android.apk", "application/vnd.android.package-archive"
		default:
			http.NotFound(w, r)
			return
		}
		file, err := os.Open(filepath.Join(releasesDir, name))
		if err != nil {
			http.NotFound(w, r)
			return
		}
		defer file.Close()
		stat, err := file.Stat()
		if err != nil || !stat.Mode().IsRegular() {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", contentType)
		w.Header().Set("Content-Disposition", `attachment; filename="`+name+`"`)
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("Cache-Control", "public, max-age=300")
		http.ServeContent(w, r, name, stat.ModTime(), file)
	})
	mux.HandleFunc("GET /nearlink", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "/nearlink/", http.StatusPermanentRedirect)
	})
	mux.HandleFunc("GET /nearlink/get", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "/nearlink/get/", http.StatusPermanentRedirect)
	})
	mux.HandleFunc("GET /nearlink/get/", serveGetPage)
	mux.HandleFunc("GET /nearlink/downloads/{name}", func(w http.ResponseWriter, r *http.Request) {
		name := r.PathValue("name")
		contentType := ""
		switch name {
		case "Nearlink-Windows-x64.zip":
			contentType = "application/zip"
		case "Nearlink-Android.apk":
			contentType = "application/vnd.android.package-archive"
		default:
			http.NotFound(w, r)
			return
		}
		file, err := os.Open(filepath.Join(releasesDir, name))
		if err != nil {
			http.NotFound(w, r)
			return
		}
		defer file.Close()
		w.Header().Set("Content-Type", contentType)
		w.Header().Set("Content-Disposition", `attachment; filename="`+name+`"`)
		w.Header().Set("X-Content-Type-Options", "nosniff")
		http.ServeContent(w, r, name, time.Time{}, file)
	})
	mux.HandleFunc("GET /nearlink/download/{platform}/", func(w http.ResponseWriter, r *http.Request) {
		platform := r.PathValue("platform")
		if platform != "windows" && platform != "android" {
			http.NotFound(w, r)
			return
		}
		page, err := webFiles.ReadFile("web/index.html")
		if err != nil {
			http.Error(w, "页面暂时无法加载。", http.StatusInternalServerError)
			return
		}
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.Header().Set("Cache-Control", "no-cache")
		_, _ = w.Write(page)
	})
	mux.Handle("GET /nearlink/", http.StripPrefix("/nearlink/", http.FileServer(http.FS(staticFiles))))
	mux.HandleFunc("GET /{$}", func(w http.ResponseWriter, _ *http.Request) {
		page, err := webFiles.ReadFile("web/404.html")
		if err != nil {
			http.Error(w, "页面不存在。Nearlink 官网请访问 /nearlink/", http.StatusNotFound)
			return
		}
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.WriteHeader(http.StatusNotFound)
		_, _ = w.Write(page)
	})
	mux.Handle("GET /", http.FileServer(http.FS(staticFiles)))

	server := &http.Server{
		Addr:              addr,
		Handler:           securityHeaders(requestLog(logger, mux)),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       15 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       60 * time.Second,
		MaxHeaderBytes:    1 << 20,
	}

	listener, err := net.Listen("tcp", addr)
	if err != nil {
		logger.Error("listen", "addr", addr, "error", err)
		os.Exit(1)
	}

	stopped := make(chan os.Signal, 1)
	signal.Notify(stopped, syscall.SIGINT, syscall.SIGTERM)
	go func() {
		<-stopped
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		if err := server.Shutdown(ctx); err != nil {
			logger.Error("graceful shutdown", "error", err)
		}
	}()

	logger.Info("nearlink website started", "addr", addr)
	if err := server.Serve(listener); err != nil && !errors.Is(err, http.ErrServerClosed) {
		logger.Error("serve", "error", err)
		os.Exit(1)
	}
}

func serveGetPage(w http.ResponseWriter, r *http.Request) {
	page, err := webFiles.ReadFile("web/index.html")
	if err != nil {
		http.Error(w, "页面暂时无法加载。", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-cache")
	_, _ = w.Write(page)
}

func env(key, fallback string) string {
	if value := strings.TrimSpace(os.Getenv(key)); value != "" {
		return value
	}
	return fallback
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}

func securityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		h := w.Header()
		h.Set("X-Content-Type-Options", "nosniff")
		h.Set("X-Frame-Options", "DENY")
		h.Set("Referrer-Policy", "strict-origin-when-cross-origin")
		h.Set("Permissions-Policy", "camera=(), microphone=(), geolocation=()")
		h.Set("Content-Security-Policy", "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'")
		next.ServeHTTP(w, r)
	})
}

func requestLog(logger *slog.Logger, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		started := time.Now()
		next.ServeHTTP(w, r)
		logger.Info("http request", "method", r.Method, "path", r.URL.Path, "duration_ms", time.Since(started).Milliseconds())
	})
}
