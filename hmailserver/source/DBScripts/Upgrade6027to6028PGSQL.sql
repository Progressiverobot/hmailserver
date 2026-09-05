create table hm_metricsamples
(
	metricsampleid bigserial not null primary key,
	metricsampletime timestamp not null,
	metricsamplename varchar(64) not null,
	metricsamplevalue double precision not null
);

CREATE INDEX idx_hm_metricsamples_name_time ON hm_metricsamples (metricsamplename, metricsampletime);

update hm_dbversion set value = 6028;
