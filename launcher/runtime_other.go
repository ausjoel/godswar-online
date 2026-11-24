//go:build !windows

package main

// The VC80 runtime is a Windows matter (runtime_windows.go); Wine carries its own.
func ensureRuntime(c *Config, force bool) {}
