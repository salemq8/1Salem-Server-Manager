package strictjson_test

import (
	"testing"

	"1salem.app/connect/transport/internal/strictjson"
)

func TestRejectsAmbiguousDocuments(t *testing.T) {
	type v struct {
		A string `json:"a"`
	}
	bad := map[string]string{
		"duplicate key":        `{"a":"1","a":"2"}`,
		"case-folded dup":      `{"a":"1","A":"2"}`,
		"kelvin sign key":      "{\"K\":\"1\"}",
		"trailing data":        `{"a":"1"} {}`,
		"not an object":        `["a"]`,
		"null":                 `null`,
		"invalid utf-8":        "{\"a\":\"\xff\"}",
		"nested duplicate":     `{"a":"1","b":{"c":1,"c":2}}`,
		"deep nesting":         `{"a":[[[[[[[[[[[[[[[[[[1]]]]]]]]]]]]]]]]]]}`,
		"unknown field strict": `{"a":"1","b":2}`,
	}
	for name, doc := range bad {
		var out v
		if err := strictjson.Decode([]byte(doc), &out); err == nil {
			t.Errorf("%s: accepted %s", name, doc)
		}
	}
	var out v
	if err := strictjson.DecodeAllowUnknown([]byte(`{"a":"1","b":2}`), &out); err != nil || out.A != "1" {
		t.Errorf("additive field refused: %v", err)
	}
}
