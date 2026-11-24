//go:build !windows && !(darwin && cgo)

package main

// No launcher window on this system or in a build without cgo on a Mac: the caller keeps the dialogs.
func runWindow(w, h int, title string, onEvent func(kind, x, y int) bool, ready func(window)) error {
	return errNoWindow
}
