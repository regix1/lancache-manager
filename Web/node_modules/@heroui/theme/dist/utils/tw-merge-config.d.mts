declare const COMMON_UNITS: string[];
declare const twMergeConfig: {
    theme: {
        spacing: string[];
        radius: string[];
    };
    classGroups: {
        shadow: {
            shadow: string[];
        }[];
        opacity: {
            opacity: string[];
        }[];
        "font-size": {
            text: string[];
        }[];
        "border-w": {
            border: string[];
        }[];
        "bg-image": string[];
        transition: string[];
    };
};

export { COMMON_UNITS, twMergeConfig };
